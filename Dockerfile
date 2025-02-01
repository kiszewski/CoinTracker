# Use .NET 9 as the base runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0-preview AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

# Use .NET 9 SDK for building the project
FROM mcr.microsoft.com/dotnet/sdk:9.0-preview AS build
WORKDIR /src

# Copy the project file and restore dependencies
COPY ["CoinTrackerAPI.csproj", "./"]
RUN dotnet restore "./CoinTrackerAPI.csproj"

# Copy the rest of the application files
COPY . .

# Build the application
RUN dotnet publish "./CoinTrackerAPI.csproj" -c Release -o /app/publish

# Use the base runtime image for the final container
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# Set the entry point
ENTRYPOINT ["dotnet", "CoinTrackerAPI.dll"]
