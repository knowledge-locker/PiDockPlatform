#!/bin/sh
set -eu
umask 077
cd "$(dirname "$0")/.."
if [ -e .state/pki/ca.key ] || [ -e .env ]; then
  echo "Preview state already exists; refusing to replace identity material or .env." >&2
  exit 1
fi
mkdir -p .state/pki .state/cloud .state/broker
openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 30 \
  -keyout .state/pki/ca.key -out .state/pki/ca.crt -subj '/CN=PiDock local preview CA' \
  -addext 'basicConstraints=critical,CA:TRUE' -addext 'keyUsage=critical,keyCertSign,cRLSign' 2>/dev/null
issue() {
  name="$1"; cn="$2"; eku="$3"; san="$4"
  openssl req -newkey rsa:2048 -nodes -sha256 -keyout ".state/pki/$name.key" \
    -out ".state/pki/$name.csr" -subj "/CN=$cn" 2>/dev/null
  printf 'basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=%s\nsubjectAltName=%s\n' "$eku" "$san" > ".state/pki/$name.ext"
  openssl x509 -req -in ".state/pki/$name.csr" -CA .state/pki/ca.crt -CAkey .state/pki/ca.key \
    -CAcreateserial -CAserial .state/pki/ca.srl -out ".state/pki/$name.crt" -days 14 -sha256 -extfile ".state/pki/$name.ext" 2>/dev/null
  rm ".state/pki/$name.csr" ".state/pki/$name.ext"
}
issue broker broker serverAuth 'DNS:broker,DNS:localhost,IP:127.0.0.1'
issue web localhost serverAuth 'DNS:localhost,IP:127.0.0.1'
issue worker pidock-ingest clientAuth 'URI:urn:pidock:worker'
issue device local--pi5 clientAuth 'URI:urn:pidock:device:local:pi5'
printf 'PIDOCK_UID=%s\nPIDOCK_GID=%s\n' "$(id -u)" "$(id -g)" > .env
chmod 600 .state/pki/*.key
printf '%s\n' 'Local preview certificates created. They expire after 14 days.' \
  'No OS trust store was changed. No user accounts were created.' \
  'Next: docker compose --env-file .env -f deploy/compose.yaml up --build -d'
