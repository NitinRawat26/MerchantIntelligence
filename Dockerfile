# Stage 1: Angular workbench
FROM node:20-alpine AS web
WORKDIR /web
COPY web/mcc-validator/package*.json ./
RUN npm ci --no-audit --no-fund
COPY web/mcc-validator/ ./
RUN npm run build -- --configuration production

# Stage 2: .NET API
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY MerchantIntelligence.sln ./
COPY src/ src/
COPY models/ models/
RUN dotnet restore src/MerchantIntelligence.Api/MerchantIntelligence.Api.csproj
RUN dotnet publish src/MerchantIntelligence.Api/MerchantIntelligence.Api.csproj \
    -c Release -o /app --no-restore

# Stage 3: runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN apt-get update && apt-get install -y --no-install-recommends libgomp1 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
COPY --from=web /web/dist/mcc-validator/browser ./wwwroot
RUN mkdir -p data
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    DOTNET_GCHeapHardLimit=0x1A000000 \
    PORT=8080
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet MerchantIntelligence.Api.dll --urls http://0.0.0.0:${PORT}"]
