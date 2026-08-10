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
generate_server_bundle() {
    local name=$1
    local common_name=$2
    local san=$3
    local days=$4

    openssl req -newkey rsa:2048 -nodes \
        -keyout "$tls_dir/$name.key" -out "$tls_dir/$name.csr" \
        -subj "/CN=$common_name" \
        >/dev/null 2>&1

    printf '%s\n' \
        'basicConstraints=critical,CA:false' \
        'keyUsage=critical,digitalSignature,keyEncipherment' \
        'extendedKeyUsage=serverAuth' \
        "subjectAltName=$san" \
        'authorityKeyIdentifier=keyid,issuer' \
        >"$tls_dir/$name.ext"

    local serial_args=(-CAserial "$tls_dir/ca.srl")
    if [[ ! -f "$tls_dir/ca.srl" ]]; then
        serial_args+=(-CAcreateserial)
    fi
    openssl x509 -req -in "$tls_dir/$name.csr" \
        -CA "$tls_dir/ca.pem" -CAkey "$tls_dir/ca.key" \
        "${serial_args[@]}" -out "$tls_dir/$name.pem" -days "$days" -sha256 \
        -extfile "$tls_dir/$name.ext" \
        >/dev/null 2>&1

    openssl pkcs12 -export -out "$tls_dir/$name.pfx" \
        -inkey "$tls_dir/$name.key" -in "$tls_dir/$name.pem" \
        -passout "pass:$cert_password" \
        >/dev/null 2>&1
}

openssl req -x509 -newkey rsa:2048 -nodes \
    -keyout "$tls_dir/ca.key" -out "$tls_dir/ca.pem" -days 2 \
    -subj "$ca_subject" \
    -addext "basicConstraints=critical,CA:true,pathlen:1" \
    -addext "keyUsage=critical,keyCertSign,cRLSign" \
    -addext "subjectKeyIdentifier=hash" \
    >/dev/null 2>&1

generate_server_bundle server garnet DNS:garnet 2
generate_server_bundle rotated garnet-rotated DNS:garnet 2
generate_server_bundle wrong-name not-garnet DNS:not-garnet 2
generate_server_bundle expired garnet DNS:garnet 0

# Rootless containers cannot traverse mktemp's 0700 directory or read 0600
# certificate files. Only the public CA and server bundle are mounted; the CA
# key and server key remain in this process-local directory and are removed on
# exit.
chmod 755 "$tls_dir"
chmod 644 "$tls_dir/ca.pem" "$tls_dir/server.pfx"

GARNET_TLS=true \
GARNET_TLS_ROTATION=true \
GARNET_CA_CERT_FILE="$tls_dir/ca.pem" \
GARNET_SERVER_CERT_FILE="$tls_dir/server.pfx" \
GARNET_SERVER_CERT_PASSWORD="$cert_password" \
GARNET_SERVER_NAME="${GARNET_SERVER_NAME:-garnet}" \
GARNET_CERT_REFRESH_SECONDS="${GARNET_CERT_REFRESH_SECONDS:-5}" \
GARNET_SERVER_CERT_ROTATED_FILE="$tls_dir/rotated.pfx" \
GARNET_SERVER_CERT_WRONG_NAME_FILE="$tls_dir/wrong-name.pfx" \
GARNET_SERVER_CERT_EXPIRED_FILE="$tls_dir/expired.pfx" \
    "$stack_dir/smoke.sh" "$@"
