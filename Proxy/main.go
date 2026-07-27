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
	"strings"
	"sync"
	"time"

	"github.com/golang-jwt/jwt/v5"
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
	visitors = make(map[string]*rate.Limiter)
	mu       sync.Mutex
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

	res,err := resource.New(ctx,
	resource.WithAttributes(semconv.ServiceNameKey.String(serviceName)))
	if err != nil {
		return nil, err
	}
	tprovider := sdktrace.NewTracerProvider(sdktrace.WithBatcher(exporter),
sdktrace.WithResource(res))
 otel.SetTracerProvider(tprovider)
 otel.SetTextMapPropagator(propagation.NewCompositeTextMapPropagator(propagation.TraceContext{},propagation.Baggage{}))
	return tprovider, nil 
}

func getVisitor(ip string) *rate.Limiter {
	mu.Lock()
	defer mu.Unlock()

	limiter, exists := visitors[ip]
	if !exists {
		limiter = rate.NewLimiter(2, 30)
		visitors[ip] = limiter
	}
	return limiter
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

func dnsDiscovery(Hostname, port string) {
	for {
		ips, err := net.LookupIP(Hostname)
		if err != nil {
			log.Printf("DNS lookup failed for %s: %v", Hostname, err)
		} else {
			var backends []*proxy.Backend
			existingBackends := serverPool.GetBackends()
			existingBackendMap := make(map[string]*proxy.Backend)
			for _, backend := range existingBackends {
				existingBackendMap[backend.URL.String()] = backend
			}
			for _, ip := range ips {

				if ip.To4() != nil {
					hostAndPort := fmt.Sprintf("%s:%s", ip.String(), port)
					if existingBackend, exists := existingBackendMap[hostAndPort]; exists {
						backends = append(backends, existingBackend)
					} else {
						serverURL, _ := url.Parse(fmt.Sprintf("http://%s", hostAndPort))
						proxyengine := httputil.NewSingleHostReverseProxy(serverURL)
						proxyengine.Transport = otelhttp.NewTransport(http.DefaultTransport)
						backend := &proxy.Backend{
							URL:          serverURL,
							Alive:        true,
							ReverseProxy: proxyengine,
						}
						backends = append(backends, backend)
					}
				}
			}
			serverPool.SetBackends(backends)
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
	go dnsDiscovery(apiHostname, apiPort)
	time.Sleep(5 * time.Second)

	port := ":8081"
	server := http.Server{
		Addr:    port,
		Handler:otelhttp.NewHandler( rateLimitMiddleware(jwtMiddleware(loadBalancer)),"Go-Reverse-proxy"),
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