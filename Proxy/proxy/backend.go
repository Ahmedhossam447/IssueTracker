package proxy

import (
	"log"
	"net"
	"net/http/httputil"
	"net/url"
	"sync"
	"sync/atomic"
	"time"
)

type CircuitState int

const (
	CircuitClosed CircuitState = iota
	CircuitOpen
	CircuitHalfOpen
)

type Backend struct {
	URL              *url.URL
	Alive            bool
	mux              sync.RWMutex
	ReverseProxy     *httputil.ReverseProxy
	state            CircuitState
	failures         int
	openUntil        time.Time
	halfOpenInFlight bool
}

const (
	circuitFailureThreshold = 3
	circuitOpenDuration     = 15 * time.Second
)

func (b *Backend) SetAlive(alive bool) {
	b.mux.Lock()
	b.Alive = alive
	b.mux.Unlock()
}

func (b *Backend) IsAlive() bool {
	b.mux.RLock()
	defer b.mux.RUnlock()
	return b.Alive
}

// AllowRequest enforces the 3-state Circuit Breaker (Closed -> Open -> Half-Open).
func (b *Backend) AllowRequest() bool {
	b.mux.Lock()
	defer b.mux.Unlock()

	switch b.state {
	case CircuitClosed:
		return true
	case CircuitOpen:
		if time.Now().After(b.openUntil) {
			b.state = CircuitHalfOpen
			b.halfOpenInFlight = true
			log.Printf("[CircuitBreaker] HALF-OPEN probe allowed for %s", b.URL.String())
			return true
		}
		return false
	case CircuitHalfOpen:
		if !b.halfOpenInFlight {
			b.halfOpenInFlight = true
			return true
		}
		return false
	default:
		return true
	}
}

func (b *Backend) RecordSuccess() {
	b.mux.Lock()
	defer b.mux.Unlock()

	if b.state != CircuitClosed {
		log.Printf("[CircuitBreaker] CLOSED (recovered) for %s", b.URL.String())
	}
	b.state = CircuitClosed
	b.failures = 0
	b.halfOpenInFlight = false
}

func (b *Backend) RecordFailure() {
	b.mux.Lock()
	defer b.mux.Unlock()

	b.failures++
	b.halfOpenInFlight = false
	if b.state == CircuitHalfOpen || b.failures >= circuitFailureThreshold {
		if b.state != CircuitOpen {
			log.Printf("[CircuitBreaker] OPENED for %s after %d consecutive failures (cooldown %s)",
				b.URL.String(), b.failures, circuitOpenDuration)
		}
		b.state = CircuitOpen
		b.openUntil = time.Now().Add(circuitOpenDuration)
	}
}

type ServerPool struct {
	mux      sync.RWMutex
	backends []*Backend
	current  uint64
}

func (s *ServerPool) SetBackends(backends []*Backend) {
	s.mux.Lock()
	s.backends = backends
	s.mux.Unlock()
}

func (s *ServerPool) GetBackends() []*Backend {
	s.mux.RLock()
	defer s.mux.RUnlock()
	return s.backends
}

func (s *ServerPool) NextIndex() int {
	return int(atomic.AddUint64(&s.current, uint64(1)) % uint64(len(s.backends)))
}

func (s *ServerPool) GetNextPeer() *Backend {
	s.mux.RLock()
	defer s.mux.RUnlock()
	if len(s.backends) == 0 {
		return nil
	}
	next := int(atomic.AddUint64(&s.current, uint64(1)) % uint64(len(s.backends)))
	l := len(s.backends) + next
	for i := next; i < l; i++ {
		idx := i % len(s.backends)
		if s.backends[idx].IsAlive() && s.backends[idx].AllowRequest() {
			if i != next {
				atomic.StoreUint64(&s.current, uint64(idx))
			}
			return s.backends[idx]
		}
	}
	return nil
}

func pingBackend(u *url.URL) bool {
	timeout := 2 * time.Second
	conn, err := net.DialTimeout("tcp", u.Host, timeout)
	if err != nil {
		return false
	}
	_ = conn.Close()
	return true
}

func (s *ServerPool) HealthCheck() {
	for _, b := range s.backends {
		alive := pingBackend(b.URL)
		b.SetAlive(alive)
		status := "up"
		if !alive {
			status = "down"
		}
		log.Printf("Backend %s is %s", b.URL.String(), status)
	}
}
