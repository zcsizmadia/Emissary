# Native AOT container for the Emissary WebApi sample.
# Build from the repository root:
#   docker build -f samples/07-WebApi/Dockerfile -t emissary-webapi .
# Run:
#   docker run --rm -p 8080:8080 -e ANTHROPIC_API_KEY=$ANTHROPIC_API_KEY emissary-webapi

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /source
COPY . .
RUN dotnet publish samples/07-WebApi -c Release -r linux-x64 -p:PublishAot=true -o /app

# runtime-deps only: no .NET runtime in the image - the binary is self-contained native code.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["./WebApi"]
