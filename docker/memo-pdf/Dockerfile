# Pricing memo → PDF (report 10 answer 3; docs/analytics/memo-pdf.md option A).
# The lossrun CLI writes memo.docx + memo.json; LibreOffice headless (MPL-2.0, separate process) renders memo.pdf.
# Build from the repo root:  docker build -f docker/memo-pdf/Dockerfile -t lossrun-memo-pdf .
# Run:  docker run --rm -v "$PWD/out:/out" lossrun-memo-pdf --payload /out/quote-payload.json --out /out/memo.docx --pdf
#   or: docker run --rm -e LOSSRUN_API_TOKEN=... lossrun-memo-pdf --quote <id> --out /out/memo.docx --pdf

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ ./
COPY docs/ /docs/
COPY config/ /config/
RUN dotnet publish LossRun.Tool -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends libreoffice-writer-nogui fonts-liberation2 \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app /app
ENV LOSSRUN_SOFFICE=soffice HOME=/tmp
WORKDIR /out
ENTRYPOINT ["dotnet", "/app/lossrun.dll", "report"]
