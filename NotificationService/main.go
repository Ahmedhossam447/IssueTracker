package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"net"
	"net/http"
	"notificationservice/activitypb"
	"os"
	"strings"
	"sync"
	"time"

	"github.com/gorilla/websocket"
	"github.com/redis/go-redis/v9"
	"go.mongodb.org/mongo-driver/bson"
	"go.mongodb.org/mongo-driver/mongo"
	"go.mongodb.org/mongo-driver/mongo/options"
	"google.golang.org/grpc"
)

var (
	upgrader = websocket.Upgrader{
		CheckOrigin: func(r *http.Request) bool { return true }, // Allow all origins for dev
	}
	// We need a thread-safe map to keep track of all active browser connections
	clients      = make(map[string]map[*websocket.Conn]bool)
	clientsMutex sync.Mutex
)

type server struct {
	activitypb.UnimplementedActivityLoggerServer
	collection  *mongo.Collection
	redisClient *redis.Client
}

func (s *server) LogActivity(ctx context.Context, req *activitypb.ActivityRequest) (*activitypb.ActivityResponse, error) {

	fmt.Printf(" Received Activity: [%s] Issue %s by %s at %s\n",
		req.Action, req.IssueId, req.UserEmail, req.Timestamp)

	document := bson.M{
		"action":     req.Action,
		"issue_id":   req.IssueId,
		"user_email": req.UserEmail,
		"timestamp":  req.Timestamp,
		"logged_at":  time.Now(),
	}
	_, err := s.collection.InsertOne(ctx, document)
	if err != nil {
		fmt.Printf(" Failed to insert into Mongo: %v\n", err)
		return nil, err
	}
	payloadBytes, _ := json.Marshal(document)
	err2 := s.redisClient.Publish(ctx, fmt.Sprintf("issue-%s-updates", req.IssueId), string(payloadBytes)).Err()
	if err2 != nil {
		fmt.Printf("❌ Failed to publish to Redis: %v\n", err)
	}
	return &activitypb.ActivityResponse{Success: true, Message: "Logged and Published!"}, nil
}

func handleConnections(w http.ResponseWriter, r *http.Request) {
	issueId := r.URL.Query().Get("issue-id")
	if issueId == "" {
		http.Error(w, "Bad Request", http.StatusBadRequest)
		return
	}

	ws, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		log.Printf("❌ WebSocket Upgrade Failed: %v", err)
		return
	}
	defer ws.Close()

	clientsMutex.Lock()
	if _, ok := clients[issueId]; !ok {
		clients[issueId] = make(map[*websocket.Conn]bool)
	}
	clients[issueId][ws] = true
	clientsMutex.Unlock()

	fmt.Println("🔌 New WebSocket Client Connected!")

	for {
		_, _, err := ws.ReadMessage()
		if err != nil {
			clientsMutex.Lock()
			delete(clients[issueId], ws)
			if len(clients[issueId]) == 0 {
				delete(clients, issueId)
			}
			clientsMutex.Unlock()
			fmt.Println("🛑 WebSocket Client Disconnected")
			break
		}
	}
}

func startRedisSubscribe(rdb *redis.Client) {
	ctx := context.Background()
	pubsub := rdb.PSubscribe(ctx, "issue-*-updates")
	defer pubsub.Close()

	ch := pubsub.Channel()

	for msg := range ch {
		fmt.Printf("📡 Redis Broadcast: %s\n", msg.Payload)
		issueId := strings.TrimSuffix(strings.TrimPrefix(msg.Channel, "issue-"), "-updates")

		clientsMutex.Lock()
		for client := range clients[issueId] {

			err := client.WriteMessage(websocket.TextMessage, []byte(msg.Payload))
			if err != nil {
				client.Close()
				delete(clients[issueId], client)
				if len(clients[issueId]) == 0 {
					delete(clients, issueId)
				}
			}

		}
		clientsMutex.Unlock()
	}
}

func main() {

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	mongoURI := os.Getenv("MONGO_URI")
	if mongoURI == "" {
		mongoURI = "mongodb://admin:AdminPassword123!@mongo:27017"
	}
	clientOptions := options.Client().ApplyURI(mongoURI)
	client, err := mongo.Connect(ctx, clientOptions)
	if err != nil {
		log.Fatalf("Failed to connect to MongoDB: %v", err)
	}
	fmt.Println("Connected to MongoDB!")

	collection := client.Database("AuditDB").Collection("activity_logs")

	redisURI := os.Getenv("REDIS_URI")

	if redisURI == "" {
		redisURI = "localhost:6379"
	}
	RedisClient := redis.NewClient(&redis.Options{Addr: redisURI})

	go startRedisSubscribe(RedisClient)

	go func() {
		http.HandleFunc("/ws", handleConnections)
		fmt.Println("🌐 WebSocket Server listening on port 8082...")
		if err := http.ListenAndServe(":8082", nil); err != nil {
			log.Fatalf("failed to start WebSocket server: %v", err)
		}

	}()
	lis, err := net.Listen("tcp", ":50051")

	if err != nil {
		log.Fatalf("Failed to listen: %v", err)
	}
	fmt.Printf("Go gRPC Notification Service running on port 50051...\n")

	s := grpc.NewServer()
	activitypb.RegisterActivityLoggerServer(s, &server{
		collection:  collection,
		redisClient: RedisClient,
	})
	if err := s.Serve(lis); err != nil {
		log.Fatalf("Failed to serve: %v", err)
	}

}
