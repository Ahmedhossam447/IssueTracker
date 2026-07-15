package main

import (
	"context"
	"fmt"
	"log"
	"net"
	"os"
	"time"
	"go.mongodb.org/mongo-driver/bson"
	"notificationservice/activitypb"
	"go.mongodb.org/mongo-driver/mongo"
	"go.mongodb.org/mongo-driver/mongo/options"
	"google.golang.org/grpc"
)

type server struct {
	activitypb.UnimplementedActivityLoggerServer
	collection *mongo.Collection
}

func (s *server) LogActivity(ctx context.Context, req *activitypb.ActivityRequest) (*activitypb.ActivityResponse, error) {

	fmt.Printf(" Received Activity: [%s] Issue %s by %s at %s\n",
		req.Action, req.IssueId, req.UserEmail, req.Timestamp)

		document:= bson.M{
		"action": req.Action,
		"issue_id": req.IssueId,
		"user_email": req.UserEmail,
		"timestamp": req.Timestamp,
		"logged_at": time.Now(),
	}
	_, err := s.collection.InsertOne(ctx, document)
	if err!=nil{
		fmt.Printf(" Failed to insert into Mongo: %v\n", err)
		return nil, err
	}

		fmt.Println("Successfully archived to MongoDB!")

	return &activitypb.ActivityResponse{
		Success: true,
		Message: "Activity logged successfully",
	}, nil
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


	lis, err := net.Listen("tcp", ":50051")

	if err != nil {
		log.Fatalf("Failed to listen: %v", err)
	}
		fmt.Printf("Go gRPC Notification Service running on port 50051...\n")

	s := grpc.NewServer()
	activitypb.RegisterActivityLoggerServer(s, &server{collection: collection})
	if err := s.Serve(lis); err != nil {
		log.Fatalf("Failed to serve: %v", err)
	}

}
