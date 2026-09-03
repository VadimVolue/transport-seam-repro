#!/usr/bin/env bash
#
# Generates the lab certificate authority and the two broker certificate variants the
# demonstrations use. Everything it writes lands under ./certs, which is git-ignored:
# these are throw-away lab keys, not something to keep.
#
#   certs/ca/ca-cert.pem              CN=test-lab-ca, self-signed, signs both variants
#   certs/normal/broker-cert.pem      CN=broker-cert, SAN: DNS:localhost, DNS:broker-a
#   certs/san-loopback/broker-cert.pem  the same, plus IP Address:127.0.0.1
#
# The only difference between the two variants is that one loopback address, and that
# difference is the whole point of the lab.

set -o errexit
set -o nounset
set -o pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly script_dir
readonly certs_dir="$script_dir/certs"

readonly ca_subject='/CN=test-lab-ca'
readonly broker_subject='/CN=broker-cert'

readonly normal_san='DNS:localhost,DNS:broker-a'
readonly san_loopback_san='DNS:localhost,DNS:broker-a,IP:127.0.0.1'

rm -rf "$certs_dir"
mkdir -p "$certs_dir/ca" "$certs_dir/normal" "$certs_dir/san-loopback"

echo "[INFO] generating the lab certificate authority ($ca_subject)"
openssl req -x509 -new -nodes -newkey rsa:2048 -sha256 -days 3650 \
    -subj "$ca_subject" \
    -addext 'basicConstraints=critical,CA:TRUE' \
    -addext 'keyUsage=critical,keyCertSign,cRLSign' \
    -keyout "$certs_dir/ca/ca-key.pem" \
    -out "$certs_dir/ca/ca-cert.pem" 2>/dev/null

# Issues one broker certificate, signed by the lab CA, carrying exactly the names given.
function issue_broker_certificate
{
    local variant="$1"
    local subject_alternative_names="$2"
    local dir="$certs_dir/$variant"

    echo "[INFO] issuing the '$variant' broker certificate ($subject_alternative_names)"

    openssl req -new -nodes -newkey rsa:2048 -sha256 \
        -subj "$broker_subject" \
        -keyout "$dir/broker-key.pem" \
        -out "$dir/broker.csr" 2>/dev/null

    cat > "$dir/broker.ext" <<EXTENSIONS
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=$subject_alternative_names
EXTENSIONS

    openssl x509 -req -in "$dir/broker.csr" -sha256 -days 825 \
        -CA "$certs_dir/ca/ca-cert.pem" \
        -CAkey "$certs_dir/ca/ca-key.pem" \
        -CAcreateserial \
        -extfile "$dir/broker.ext" \
        -out "$dir/broker-cert.pem" 2>/dev/null

    rm -f "$dir/broker.csr" "$dir/broker.ext"

    # The broker runs as a non-root user inside its container and has to read the key.
    chmod 0644 "$dir/broker-key.pem" "$dir/broker-cert.pem"
}

issue_broker_certificate 'normal' "$normal_san"
issue_broker_certificate 'san-loopback' "$san_loopback_san"

chmod 0644 "$certs_dir/ca/ca-cert.pem"

echo
echo '[INFO] subject alternative names, as issued:'
for variant in normal san-loopback
do
    printf '  %-13s ' "$variant"
    openssl x509 -in "$certs_dir/$variant/broker-cert.pem" -noout -ext subjectAltName |
        tail -n +2 | tr -d ' ' | paste -sd' ' -
done
echo
echo '[INFO] done. Bring the lab up with:  docker compose up -d'
