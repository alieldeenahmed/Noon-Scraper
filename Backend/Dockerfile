# Single-stage build: the final image needs the .NET SDK anyway to run the
# Crawler's install/install-deps passthrough below, so there's no size benefit
# to a separate slim runtime stage here.
FROM mcr.microsoft.com/dotnet/sdk:9.0

WORKDIR /src

COPY NoonScraper.sln .
COPY NoonScraper.Data/ NoonScraper.Data/
COPY NoonScraper.Crawler/ NoonScraper.Crawler/
COPY NoonScraper.Api/ NoonScraper.Api/

# Installs Chrome and Playwright's system dependencies via the Crawler's own
# install/install-deps passthrough (see NoonScraper.Crawler/Program.cs) - the
# same commands used in local dev and the GitHub Actions workflow, so there's
# one source of truth for what this needs instead of a hand-maintained apt
# package list duplicated here.
RUN dotnet build NoonScraper.Crawler/NoonScraper.Crawler.csproj -c Release && \
    dotnet run --project NoonScraper.Crawler -c Release --no-build -- install chrome && \
    dotnet run --project NoonScraper.Crawler -c Release --no-build -- install-deps

RUN dotnet publish NoonScraper.Api/NoonScraper.Api.csproj -c Release -o /app

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# Chrome refuses to start as root without disabling its own sandbox - rather
# than weaken that, this runs as a normal unprivileged user. The container
# boundary is already the real security barrier here.
RUN useradd --create-home appuser && chown -R appuser:appuser /app
USER appuser

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
EXPOSE 8080

ENTRYPOINT ["/entrypoint.sh"]
