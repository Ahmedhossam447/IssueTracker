FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src



COPY ["IssueTracker/IssueTracker.API.csproj", "IssueTracker/"]
COPY ["Domain/IssueTracker.Core.csproj", "Domain/"]
COPY ["Application/IssueTracker.Application.csproj", "Application/"]
COPY ["Infrastructure/IssueTracker.Infrastructure.csproj", "Infrastructure/"]


RUN dotnet restore "IssueTracker/IssueTracker.API.csproj"

COPY . .

WORKDIR "/src/IssueTracker"

RUN dotnet build "IssueTracker.API.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "IssueTracker.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final

WORKDIR /app

COPY --from=publish /app/publish .

ENTRYPOINT ["dotnet", "IssueTracker.API.dll"]