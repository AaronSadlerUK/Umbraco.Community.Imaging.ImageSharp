# Ubuntu 24.04 (noble) based ASP.NET runtime — the environment the problem was reported on.
# Build with:  docker build --build-arg PUBLISH_DIR=out/imagesharp -t imaging-harness .
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble

ARG PUBLISH_DIR=out/imagesharp

WORKDIR /app
COPY ${PUBLISH_DIR}/ ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "ImagingHarness.dll"]
