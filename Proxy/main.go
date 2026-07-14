package main

import (
	"fmt"
	"issuetrackerproxy/proxy"
	"log"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"strings"
	"time"

	"github.com/golang-jwt/jwt/v5"
)

var serverPool proxy.ServerPool

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
		Handler: jwtMiddleware(loadBalancer),
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
