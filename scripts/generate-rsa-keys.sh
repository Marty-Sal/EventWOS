#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# EventWOS — RSA Key Generator
# Generates a 2048-bit RSA key pair and outputs Base64 values ready to paste
# into appsettings.json or docker-compose environment variables.
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

KEYS_DIR="$(dirname "$0")/../keys"
mkdir -p "$KEYS_DIR"

echo "🔐 Generating RSA 2048-bit key pair..."

# Generate private key (PKCS#1 DER)
openssl genrsa -out "$KEYS_DIR/private.pem" 2048 2>/dev/null
openssl rsa -in "$KEYS_DIR/private.pem" -outform DER -out "$KEYS_DIR/private.der" 2>/dev/null

# Extract public key (PKCS#1 DER)
openssl rsa -in "$KEYS_DIR/private.pem" -pubout -RSAPublicKey_out -outform DER -out "$KEYS_DIR/public.der" 2>/dev/null

PRIVATE_B64=$(base64 -w 0 "$KEYS_DIR/private.der")
PUBLIC_B64=$(base64  -w 0 "$KEYS_DIR/public.der")

echo ""
echo "✅ Keys generated. Paste these into your appsettings.json / .env:"
echo ""
echo "Jwt__PrivateKey=$PRIVATE_B64"
echo ""
echo "Jwt__PublicKey=$PUBLIC_B64"
echo ""
echo "⚠️  Keys saved to $KEYS_DIR — add this folder to .gitignore!"
echo "   Never commit private keys to version control."
