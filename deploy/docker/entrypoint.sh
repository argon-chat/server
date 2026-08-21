#!/bin/sh
echo "Creating Vault config..."
set -e

sleep 5

if [ ! -f /vault/unseal-1 ]; then
  echo "Initializing Vault..."
  vault operator init -key-shares=3 -key-threshold=2 > /vault/init-keys
  grep "Unseal Key 1:" /vault/init-keys | awk '{print $4}' > /vault/unseal-1
  grep "Unseal Key 2:" /vault/init-keys | awk '{print $4}' > /vault/unseal-2
  grep "Unseal Key 3:" /vault/init-keys | awk '{print $4}' > /vault/unseal-3
  grep "Initial Root Token:" /vault/init-keys | awk '{print $4}' > /vault/token
  chmod 600 /vault/unseal-* /vault/token
  echo "Vault initialized."
fi

echo "Unsealing Vault..."
vault operator unseal $(cat /vault/unseal-1)
vault operator unseal $(cat /vault/unseal-2)

echo "Vault is ready."
sleep infinity