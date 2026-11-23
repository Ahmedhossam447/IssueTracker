package main

import (
	"context"
	"fmt"
	"issuetrackerproxy/proxy"
	"log"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/golang-jwt/jwt/v5"
	clientv3 "go.etcd.io/etcd/client/v3"
	"go.opentelemetry.io/contrib/instrumentation/net/http/otelhttp"
	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracegrpc"
	"go.opentelemetry.io/otel/propagation"
	"go.opentelemetry.io/otel/sdk/resource"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	semconv "go.opentelemetry.io/otel/semconv/v1.26.0"
	"golang.org/x/time/rate"
)

var serverPool proxy.ServerPool

var (
	visitors     = make(map[string]*rate.Limiter)
	mu           sync.Mutex
	currentRPS   = 2.0
	currentBurst = 30
	inFlight     int64
	maxInFlight  int64 = 150
)

func initTracer(serviceName, collectorAddr string) (*sdktrace.TracerProvider, error) {
	ctx := context.Background()
	exporter, err := otlptracegrpc.New(ctx,
		otlptracegrpc.WithInsecure(),
		otlptracegrpc.WithEndpoint(collectorAddr),
	)
	if err != nil {
		return nil, err
	}

	res, err := resource.New(ctx,
		resource.WithAttributes(semconv.ServiceNameKey.String(serviceName)))
	if err != nil {
		return nil, err
	}
	tprovider := sdktrace.NewTracerProvider(sdktrace.WithBatcher(exporter),
		sdktrace.WithResource(res))
	otel.SetTracerProvider(tprovider)
	otel.SetTextMapPropagator(propagation.NewCompositeTextMapPropagator(propagation.TraceContext{}, propagation.Baggage{}))
	return tprovider, nil
}

func getVisitor(ip string) *rate.Limiter {
	mu.Lock()
	defer mu.Unlock()

	limiter, exists := visitors[ip]
	if !exists {
		limiter = rate.NewLimiter(rate.Limit(currentRPS), currentBurst)
		visitors[ip] = limiter
	}
	return limiter
}

func updateRateLimit(rps *float64, burst *int) {
	mu.Lock()
	defer mu.Unlock()

	if rps != nil && *rps > 0 {
		currentRPS = *rps
	}
	if burst != nil && *burst > 0 {
		currentBurst = *burst
	}
	for _, limiter := range visitors {
		limiter.SetLimit(rate.Limit(currentRPS))
		limiter.SetBurst(currentBurst)
	}
	log.Printf("[etcd-Config] Hot-reloaded Token Bucket Rate Limiter: RPS=%.1f, Burst=%d", currentRPS, currentBurst)
}

func isLowPriorityRequest(r *http.Request) bool {
	if r.Method != http.MethodGet {
		return false
	}
	path := r.URL.Path
	return strings.HasPrefix(path, "/api/Issue/search") ||
		strings.HasPrefix(path, "/api/Issue/feed") ||
		path == "/api/Issue"
}

// DDIA Ch. 2: Priority-Based Server-Side Load Shedding with 50ms micro-burst grace window
// and Retry-After backpressure header.
func loadSheddingMiddleware(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		lowPriority := isLowPriorityRequest(r)
		deadline := time.Now().Add(50 * time.Millisecond)

		for {
			maxCap := atomic.LoadInt64(&maxInFlight)
			limit := maxCap
			if lowPriority {
				limit = int64(float64(maxCap) * 0.70)
				if limit < 1 {
					limit = 1
				}
			}

			cur := atomic.LoadInt64(&inFlight)
			if cur < limit {
				if atomic.CompareAndSwapInt64(&inFlight, cur, cur+1) {
					defer atomic.AddInt64(&inFlight, -1)
					next(w, r)
					return
				}
				continue
			}

			if time.Now().After(deadline) {
				tier := "HIGH"
				if lowPriority {
					tier = "LOW"
				}
				log.Printf("[LoadShedding] Shedding %s-priority %s %s (inFlight=%d, limit=%d/%d)",
					tier, r.Method, r.URL.Path, cur, limit, maxCap)
				w.Header().Set("Retry-After", "2")
				http.Error(w, "Service Unavailable - Server Overloaded", http.StatusServiceUnavailable)
				return
			}
			time.Sleep(10 * time.Millisecond)
		}
	}
}

func rateLimitMiddleware(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		ip, _, err := net.SplitHostPort(r.RemoteAddr)
		if err != nil {
			log.Printf("Error parsing IP from RemoteAddr: %v", err)
			http.Error(w, "Internal Server Error", http.StatusInternalServerError)
			return
		}
		limiter := getVisitor(ip)
		if !limiter.Allow() {
			log.Printf("Rate limit exceeded for IP: %s", ip)
			http.Error(w, "Too Many Requests", http.StatusTooManyRequests)
			return
		}
		next(w, r)
	}
}

var jwtSecret = []byte("SuperSecretKeyThatIsAtLeast32BytesLong123!")

func jwtMiddleware(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if strings.HasPrefix(r.URL.Path, "/api/Auth") || strings.HasPrefix(r.URL.Path, "/swagger") {
			next(w, r)
			return
		}

		authHeader := r.Header.Get("Authorization")
		if authHeader == "" {
			log.Printf("[Auth] Blocked request with MISSING token from %s", r.RemoteAddr)
			http.Error(w, "Missing Authorization Token", http.StatusUnauthorized)
			return
		}
		tokenstring := strings.TrimPrefix(authHeader, "Bearer ")
		token, err := jwt.Parse(tokenstring, func(token *jwt.Token) (interface{}, error) {
			if _, ok := token.Method.(*jwt.SigningMethodHMAC); !ok {
				return nil, fmt.Errorf("Unexpected signing method: %v", token.Header["alg"])
			}
			return jwtSecret, nil
		})
		if err != nil || !token.Valid {
			log.Printf("Invalid token from %s", r.RemoteAddr)
			http.Error(w, "Invalid Authorization Token", http.StatusUnauthorized)
			return
		}
		log.Printf("[Auth] Token validated successfully! Routing to .NET API...")
		next(w, r)
	}
}

func loadBalancer(w http.ResponseWriter, r *http.Request) {
	peer := serverPool.GetNextPeer()
	if peer != nil {
		peer.ReverseProxy.ServeHTTP(w, r)
		return
	}
	http.Error(w, "Service not available", http.StatusServiceUnavailable)
}

func newBackend(rawURL string) (*proxy.Backend, error) {
	serverURL, err := url.Parse(rawURL)
	if err != nil {
		return nil, err
	}
	proxyengine := httputil.NewSingleHostReverseProxy(serverURL)
	proxyengine.Transport = otelhttp.NewTransport(http.DefaultTransport)
	b := &proxy.Backend{
		URL:          serverURL,
		Alive:        true,
		ReverseProxy: proxyengine,
	}

	// DDIA Ch. 2 & 9: Feed HTTP 5xx and transport errors into the Per-Backend Circuit Breaker
	proxyengine.ModifyResponse = func(resp *http.Response) error {
		if resp.StatusCode >= 500 {
			b.RecordFailure()
		} else {
			b.RecordSuccess()
		}
		return nil
	}
	proxyengine.ErrorHandler = func(w http.ResponseWriter, r *http.Request, err error) {
		b.RecordFailure()
		log.Printf("[Proxy] Backend error (%s): %v", serverURL.String(), err)
		http.Error(w, "Bad Gateway", http.StatusBadGateway)
	}

	return b, nil
}

// DDIA Ch. 10 (Coordination Service): Real-time Service Discovery via etcd Watches.
// Replaces 10s DNS polling with instant PUT/DELETE notifications as .NET API replicas join or crash.
func etcdWatchServices(cli *clientv3.Client) {
	const prefix = "/services/api/"
	backendsMap := make(map[string]*proxy.Backend)
	syncPool := func() {
		list := make([]*proxy.Backend, 0, len(backendsMap))
		for _, b := range backendsMap {
			list = append(list, b)
		}
		serverPool.SetBackends(list)
	}

	// 1. Initial load of all currently registered ephemeral nodes
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	resp, err := cli.Get(ctx, prefix, clientv3.WithPrefix())
	cancel()
	if err != nil {
		log.Printf("[etcd-Discovery] Initial Get failed: %v", err)
	} else {
		for _, kv := range resp.Kvs {
			b, err := newBackend(string(kv.Value))
			if err == nil {
				backendsMap[string(kv.Key)] = b
				log.Printf("[etcd-Discovery] Loaded initial backend %s -> %s", string(kv.Key), string(kv.Value))
			}
		}
		if len(backendsMap) > 0 {
			syncPool()
		}
	}

	// 2. Watch for real-time PUT / DELETE events
	rch := cli.Watch(context.Background(), prefix, clientv3.WithPrefix())
	for wresp := range rch {
		changed := false
		for _, ev := range wresp.Events {
			key := string(ev.Kv.Key)
			switch ev.Type {
			case clientv3.EventTypePut:
				val := string(ev.Kv.Value)
				b, err := newBackend(val)
				if err == nil {
					backendsMap[key] = b
					changed = true
					log.Printf("[etcd-Watch] PUT backend %s -> %s", key, val)
				}
			case clientv3.EventTypeDelete:
				if _, ok := backendsMap[key]; ok {
					delete(backendsMap, key)
					changed = true
					log.Printf("[etcd-Watch] DELETE backend %s (lease expired or shutdown)", key)
				}
			}
		}
		if changed {
			syncPool()
		}
	}
}

// DDIA Ch. 10: Dynamic Cluster Configuration via etcd Watches (zero-downtime rate-limit tuning).
func etcdWatchConfig(cli *clientv3.Client) {
	const rpsKey = "/config/proxy/rate_limit_rps"
	const burstKey = "/config/proxy/rate_limit_burst"
	const maxInFlightKey = "/config/proxy/max_inflight"

	applyKV := func(k, v string) {
		if k == rpsKey {
			if val, err := strconv.ParseFloat(v, 64); err == nil {
				updateRateLimit(&val, nil)
			}
		} else if k == burstKey {
			if val, err := strconv.Atoi(v); err == nil {
				updateRateLimit(nil, &val)
			}
		} else if k == maxInFlightKey {
			if val, err := strconv.ParseInt(v, 10, 64); err == nil && val > 0 {
				atomic.StoreInt64(&maxInFlight, val)
				log.Printf("[etcd-Config] Hot-reloaded Load Shedding maxInFlight=%d (low-priority sheds at %d)",
					val, int64(float64(val)*0.70))
			}
		}
	}

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	resp, err := cli.Get(ctx, "/config/proxy/", clientv3.WithPrefix())
	cancel()
	if err == nil {
		for _, kv := range resp.Kvs {
			applyKV(string(kv.Key), string(kv.Value))
		}
	}

	rch := cli.Watch(context.Background(), "/config/proxy/", clientv3.WithPrefix())
	for wresp := range rch {
		for _, ev := range wresp.Events {
			if ev.Type == clientv3.EventTypePut {
				applyKV(string(ev.Kv.Key), string(ev.Kv.Value))
			}
		}
	}
}

// Fallback DNS discovery used only if no etcd ephemeral backends are registered yet.
func dnsDiscoveryFallback(Hostname, port string) {
	for {
		if len(serverPool.GetBackends()) == 0 {
			ips, err := net.LookupIP(Hostname)
			if err == nil {
				var backends []*proxy.Backend
				for _, ip := range ips {
					if ip.To4() != nil {
						rawURL := fmt.Sprintf("http://%s:%s", ip.String(), port)
						if b, err := newBackend(rawURL); err == nil {
							backends = append(backends, b)
						}
					}
				}
				if len(backends) > 0 {
					serverPool.SetBackends(backends)
				}
			}
		}
		time.Sleep(10 * time.Second)
	}
}

func main() {
	tp, err := initTracer("IssueTracker-Proxy", "jaeger:4317")
	if err != nil {
		log.Fatalf("Failed to initialize tracer: %v", err)
	}
	defer func() {
		if err := tp.Shutdown(context.Background()); err != nil {
			log.Printf("Error shutting down tracer: %v", err)
		}
	}()

	apiHostname := os.Getenv("API_HOSTNAME")
	if apiHostname == "" {
		apiHostname = "api"
	}
	apiPort := os.Getenv("API_PORT")
	if apiPort == "" {
		apiPort = "8080"
	}

	etcdEndpointsEnv := os.Getenv("ETCD_ENDPOINTS")
	if etcdEndpointsEnv == "" {
		etcdEndpointsEnv = "etcd1:2379,etcd2:2379,etcd3:2379"
	}
	etcdClient, err := clientv3.New(clientv3.Config{
		Endpoints:   strings.Split(etcdEndpointsEnv, ","),
		DialTimeout: 5 * time.Second,
	})
	if err != nil {
		log.Printf("⚠️ Failed to connect to etcd (%v), falling back to DNS discovery", err)
	} else {
		defer etcdClient.Close()
		go etcdWatchServices(etcdClient)
		go etcdWatchConfig(etcdClient)
	}

	go dnsDiscoveryFallback(apiHostname, apiPort)
	time.Sleep(3 * time.Second)

	port := ":8081"
	server := http.Server{
		Addr:    port,
		Handler: otelhttp.NewHandler(rateLimitMiddleware(loadSheddingMiddleware(jwtMiddleware(loadBalancer))), "Go-Reverse-proxy"),
	}
	fmt.Printf(" Go Reverse Proxy active on http://localhost%s\n", port)
	go func() {
		for {
			serverPool.HealthCheck()
			time.Sleep(10 * time.Second)
		}
	}()

	if err := server.ListenAndServe(); err != nil {
		log.Fatal(err)
	}
}
