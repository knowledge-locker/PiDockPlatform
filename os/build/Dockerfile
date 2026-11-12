FROM ubuntu:24.04@sha256:2198e4c65076e9cb0b3d695219df667c0e3b0072da64ca3c2648aca17dcecaf5
ENV DEBIAN_FRONTEND=noninteractive LANG=C.UTF-8 LC_ALL=C.UTF-8
RUN apt-get update && apt-get install -y --no-install-recommends \
    bc binutils bison build-essential ca-certificates cpio curl file flex gawk \
    git gzip libncurses-dev libssl-dev patch perl python3 rsync unzip wget xz-utils \
    dosfstools mtools cryptsetup-bin util-linux uuid-runtime zstd \
    && rm -rf /var/lib/apt/lists/*
RUN useradd --uid 2000 --create-home builder
USER builder
WORKDIR /work
