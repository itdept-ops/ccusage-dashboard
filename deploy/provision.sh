#!/usr/bin/env bash
# Usage IQ — one-time provisioning. Run it in AWS CloudShell (uses your console login; no keys).
#   git clone https://github.com/itdept-ops/usage-iq && cd usage-iq/deploy && bash provision.sh
set -euo pipefail

REGION=us-west-2
STACK=usage-iq

echo "==> Region: $REGION   Stack: $STACK"

# 1. App secrets live in SSM Parameter Store (encrypted). Generate once; never rotate on re-run.
ensure_secret() {
  local name=$1
  if aws ssm get-parameter --region "$REGION" --name "$name" >/dev/null 2>&1; then
    echo "    $name already set — keeping it"
  else
    local val
    val=$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9'); val=${val:0:44}
    aws ssm put-parameter --region "$REGION" --name "$name" --type SecureString --value "$val" >/dev/null
    echo "    created $name"
  fi
}
echo "==> Storing app secrets in SSM (/usage-iq/*)"
ensure_secret /usage-iq/jwt-key
ensure_secret /usage-iq/db-password

# 2. Use your account's default VPC + a subnet in it (create the default VPC if the region lacks one).
echo "==> Finding your default VPC + a subnet"
VPC=$(aws ec2 describe-vpcs --region "$REGION" --filters Name=isDefault,Values=true --query 'Vpcs[0].VpcId' --output text)
if [ "$VPC" = "None" ] || [ -z "$VPC" ]; then
  echo "    No default VPC in $REGION — creating the standard one (free, isolated)…"
  aws ec2 create-default-vpc --region "$REGION" >/dev/null
  VPC=$(aws ec2 describe-vpcs --region "$REGION" --filters Name=isDefault,Values=true --query 'Vpcs[0].VpcId' --output text)
fi
SUBNET=$(aws ec2 describe-subnets --region "$REGION" --filters Name=vpc-id,Values="$VPC" --query 'Subnets[0].SubnetId' --output text)
echo "    VPC=$VPC  Subnet=$SUBNET"

# 3. Create everything (idempotent — safe to re-run).
echo "==> Creating infrastructure (a few minutes)…"
aws cloudformation deploy --region "$REGION" \
  --stack-name "$STACK" \
  --template-file "$(dirname "$0")/cloudformation.yaml" \
  --capabilities CAPABILITY_IAM \
  --no-fail-on-empty-changeset \
  --parameter-overrides VpcId="$VPC" SubnetId="$SUBNET"

# 4. Show the results.
echo
echo "==> Done. Your details:"
aws cloudformation describe-stacks --region "$REGION" --stack-name "$STACK" \
  --query 'Stacks[0].Outputs' --output table
echo
echo "Copy the PublicIp value to your assistant — that's where DNS will point."
