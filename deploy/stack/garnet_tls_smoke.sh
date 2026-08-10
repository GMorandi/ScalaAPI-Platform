#!/usr/bin/env bash
set -Eeuo pipefail

stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if ! command -v openssl >/dev/null 2>&1; then
    echo "openssl is required for the Garnet TLS smoke" >&2
    exit 2
fi

tls_dir="$(mktemp -d "${TMPDIR:-/tmp}/scalaapi-garnet-tls.XXXXXX")"
cleanup() {
    local status=$?
    set +e
    rm -rf -- "$tls_dir"
    exit "$status"
}
trap cleanup EXIT

cert_password="${GARNET_SERVER_CERT_PASSWORD:-scalaapi-garnet-dev-cert}"
ca_subject="/CN=ScalaAPI Garnet smoke CA"
server_subject="/CN=garnet"

openssl req -x509 -newkey rsa:2048 -nodes \
    -keyout "$tls_dir/ca.key" -out "$tls_dir/ca.pem" -days 2 \
    -subj "$ca_subject" \
    -addext "basicConstraints=critical,CA:true,pathlen:1" \
    -addext "keyUsage=critical,keyCertSign,cRLSign" \
    -addext "subjectKeyIdentifier=hash" \
    >/dev/null 2>&1

openssl req -newkey rsa:2048 -nodes \
    -keyout "$tls_dir/server.key" -out "$tls_dir/server.csr" \
    -subj "$server_subject" \
    >/dev/null 2>&1

printf '%s\n' \
    'basicConstraints=critical,CA:false' \
    'keyUsage=critical,digitalSignature,keyEncipherment' \
    'extendedKeyUsage=serverAuth' \
    'subjectAltName=DNS:garnet' \
    'authorityKeyIdentifier=keyid,issuer' \
    >"$tls_dir/server.ext"

openssl x509 -req -in "$tls_dir/server.csr" \
    -CA "$tls_dir/ca.pem" -CAkey "$tls_dir/ca.key" \
    -CAcreateserial -out "$tls_dir/server.pem" -days 2 -sha256 \
    -extfile "$tls_dir/server.ext" \
    >/dev/null 2>&1

openssl pkcs12 -export -out "$tls_dir/server.pfx" \
    -inkey "$tls_dir/server.key" -in "$tls_dir/server.pem" \
    -passout "pass:$cert_password" \
    >/dev/null 2>&1

# Rootless containers cannot traverse mktemp's 0700 directory or read 0600
# certificate files. Only the public CA and server bundle are mounted; the CA
# key and server key remain in this process-local directory and are removed on
# exit.
chmod 755 "$tls_dir"
chmod 644 "$tls_dir/ca.pem" "$tls_dir/server.pfx"

GARNET_TLS=true \
GARNET_CA_CERT_FILE="$tls_dir/ca.pem" \
GARNET_SERVER_CERT_FILE="$tls_dir/server.pfx" \
GARNET_SERVER_CERT_PASSWORD="$cert_password" \
GARNET_SERVER_NAME="${GARNET_SERVER_NAME:-garnet}" \
    "$stack_dir/smoke.sh" "$@"
