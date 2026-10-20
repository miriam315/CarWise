ARG OCR_BASE_IMAGE=ubuntu:24.04
FROM ${OCR_BASE_IMAGE}
RUN apt-get update \
 && apt-get install --yes --no-install-recommends ca-certificates libicu74 libssl3t64 libkrb5-3 zlib1g libstdc++6 tesseract-ocr tesseract-ocr-eng tesseract-ocr-heb poppler-utils \
 && rm -rf /var/lib/apt/lists/*
COPY runtime/ /opt/dotnet/
WORKDIR /worker
COPY publish/ ./
RUN chmod -R a+rX /opt/dotnet /worker
ENV PATH="/opt/dotnet:${PATH}" DOTNET_ROOT=/opt/dotnet HOME=/tmp TMPDIR=/tmp OMP_THREAD_LIMIT=1 DOTNET_EnableDiagnostics=0
USER 65532:65532
ENTRYPOINT ["dotnet", "CarWise.Ocr.Worker.dll"]
