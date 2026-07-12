FROM golang:1.26-alpine AS builder

WORKDIR /app

COPY go.mod go.sum* ./
RUN go mod download || true

COPY . .

RUN go build -o proxy-engine main.go


FROM alpine:latest

COPY --from=builder /app/proxy-engine .

EXPOSE 8081

CMD ["./proxy-engine"]