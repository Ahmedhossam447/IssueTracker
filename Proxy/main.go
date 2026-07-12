package main

import (
	"os"
	"time"
	"fmt"
	"issuetrackerproxy/proxy"
	"log"
	"net/http"
	"net/http/httputil"
	"net/url"
)

var serverPool proxy.ServerPool

func loadBalancer(w http.ResponseWriter, r *http.Request) {
	peer := serverPool.GetNextPeer()
	if peer != nil {
		peer.ReverseProxy.ServeHTTP(w, r)
		return
	}
	http.Error(w, "Service not available", http.StatusServiceUnavailable)
}

func main() {
	apiServer := os.Getenv("BACKEND_URL")
	if apiServer == "" {
		apiServer = "http://localhost:8080"
	}
	serverURL, err := url.Parse(apiServer)
	if err != nil {
		log.Fatal(err)
	}
	proxyengine := httputil.NewSingleHostReverseProxy(serverURL)
	backend := &proxy.Backend{
		URL:          serverURL,
		Alive:        true,
		ReverseProxy: proxyengine,
	}
	serverPool.AddBackend(backend)
	port := ":8081"
	server := http.Server{
		Addr:    port,
		Handler: http.HandlerFunc(loadBalancer),
	}
	fmt.Printf(" Go Reverse Proxy active on http://localhost%s\n", port)

	fmt.Printf(" Routing traffic to .NET API at %s\n", apiServer)

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
