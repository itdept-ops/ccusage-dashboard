# Deploy (AWS, push-to-main)

Hosts Usage IQ on one small EC2 box in `us-west-2`, deployed from GitHub Actions over **OIDC**
(no stored AWS keys). Everything is tagged `Project=usage-iq` and isolated from anything else
in the account.

## What gets created
- **EC2** `t3.small` + 30 GB gp3 + **Elastic IP**, Amazon Linux 2023 with Docker (SSM-managed — no SSH).
- **Security group**: 80/443 open; no port 22 (shell access is via SSM Session Manager).
- **2 ECR repos**: `usage-iq-api`, `usage-iq-web`.
- **Instance role**: pull from ECR, read `/usage-iq/*` secrets, SSM-managed.
- **Deploy permissions** on the existing `usage-iq-deploy` OIDC role — scoped to push to those two
  repos and run a deploy on that one instance, nothing else.
- **Secrets** in SSM Parameter Store (SecureString): `/usage-iq/jwt-key`, `/usage-iq/db-password`
  (generated once).

## One-time provisioning

In the AWS console open **CloudShell** (the `>_` icon, top-right), then run:

```bash
git clone https://github.com/itdept-ops/usage-iq && cd usage-iq/deploy && bash provision.sh
```

It prints a **PublicIp** at the end — point your DNS A-record there.

To tear it all down later: `aws cloudformation delete-stack --region us-west-2 --stack-name usage-iq`
(plus deleting the two ECR repos if they still hold images).

## Deploying the app
On every push to `main`, `.github/workflows/deploy.yml` builds the API + web images, pushes them to
ECR, and rolls them out on the instance via SSM. The instance reads its secrets from SSM at start.
