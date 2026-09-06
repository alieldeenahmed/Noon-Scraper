# The API no longer launches a browser itself (see check-now.yml / the
# GitHubDispatchService) - it's a plain ASP.NET Core app now, so this is a
# normal slim multi-stage build with no Chrome/Xvfb/system-package install
# step. Only NoonScraper.Data and NoonScraper.Api are needed; the Crawler
# project (where Chrome-launching code actually lives) isn't referenced by
# the API at all, so it's never copied into this image.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY NoonScraper.Data/NoonScraper.Data.csproj NoonScraper.Data/
COPY NoonScraper.Api/NoonScraper.Api.csproj NoonScraper.Api/
RUN dotnet restore NoonScraper.Api/NoonScraper.Api.csproj

COPY NoonScraper.Data/ NoonScraper.Data/
COPY NoonScraper.Api/ NoonScraper.Api/
RUN dotnet publish NoonScraper.Api/NoonScraper.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

RUN useradd --create-home appuser && chown -R appuser:appuser /app
USER appuser

EXPOSE 8080
ENTRYPOINT ["/entrypoint.sh"]
