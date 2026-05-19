# Setup — AWS S3 + CloudFront

One-time setup for the Content Repo upload pipeline using the bundled `AwsUploadProvider`. Replace `your-bucket-name`, `your-account-id`, and `eu-central-1` with your values throughout.

---

## 1. Create an AWS account

1. Go to **https://aws.amazon.com** and click **Create an AWS Account**.
2. Enter your email address and choose an account name (e.g. `my-studio`).
3. Choose **Personal** or **Business** — either works.
4. Enter payment details. AWS won't charge you unless you exceed the free tier.
5. Complete phone verification and select the **Free** support plan.

You are now signed in as the **root user**. The root user has unrestricted access to everything and should not be used for day-to-day work.

## 2. Secure the root account

1. In the top-right corner, open the account menu and go to **Security credentials**.
2. Under **Multi-factor authentication (MFA)**, click **Assign MFA device** and follow the prompts to register an authenticator app.
3. After saving MFA, sign out. From this point forward, use an IAM admin user (created in step 3) for all work — reserve root login for account-level emergencies only.

## 3. Create an IAM admin user

This user runs all the setup CLI commands in steps 5–7. After setup you can delete it or keep it for future infrastructure changes.

1. In the AWS console, search for **IAM** and open it.
2. Click **Users → Create user**.
3. Name it `admin` (or anything you like), then click **Next**.
4. Choose **Attach policies directly**, search for `AdministratorAccess`, tick it, then click **Next → Create user**.
5. Open the user, go to the **Security credentials** tab, and click **Create access key**.
6. Select **Command Line Interface (CLI)**, acknowledge the warning, and click **Next → Create access key**.
7. **Copy and save the Access Key ID and Secret Access Key** — the secret is shown only once.

## 4. Install the AWS CLI

- **macOS:** `brew install awscli`
- **Windows:** download and run `https://awscli.amazonaws.com/AWSCLIV2.msi`, then open a new terminal.
- **Linux (Debian/Ubuntu):** `sudo apt-get install awscli`

Verify:

```bash
aws --version
# aws-cli/2.x.x ...
```

## 5. Configure the admin credentials

```bash
aws configure
```

Enter:

- **AWS Access Key ID** — from step 3
- **AWS Secret Access Key** — from step 3
- **Default region name** — e.g. `eu-central-1`
- **Default output format** — `json`

Test that everything works:

```bash
aws sts get-caller-identity
```

You should see your account ID and the `admin` user ARN.

---

## 6. Create the S3 bucket

1. In the AWS console, search for **S3** and open it.
2. Click **Create bucket**.
3. Enter a **Bucket name** (e.g. `my-studio-content`) — must be globally unique.
4. Set **AWS Region** to your target region (e.g. `eu-central-1`).
5. Under **Object Ownership**, leave it at **ACLs disabled**.
6. Under **Block Public Access settings**, leave all four checkboxes ticked. CloudFront will read the bucket via Origin Access Control — public access is not needed.
7. Leave all other settings at their defaults and click **Create bucket**.

## 7. Create the CloudFront distribution

1. In the AWS console, search for **CloudFront** and open it.
2. Click **Create distribution**. If asked to choose a plan, select **Standard** and click **Next**.

**Step 2 — Get started**

3. Enter a **Distribution name** (e.g. `content-repo`).
4. Keep **Distribution type** set to **Single website or app**.
5. Leave the **Domain** field empty and click **Next**.

**Step 3 — Specify origin**

6. Under **Origin type**, keep **Amazon S3** selected.
7. Click **Browse S3** and select your bucket, or type its URL directly (e.g. `your-bucket-name.s3.eu-central-1.amazonaws.com`).
8. Under **Settings**, tick **Allow private S3 bucket access to CloudFront — Recommended**. This creates the Origin Access Control automatically.
9. Leave **Use recommended origin settings** selected.
10. Click **Next** through **Enable security**, leaving defaults, then click **Create distribution** on the review page.

AWS will show a banner: **"You must update the S3 bucket policy"**. Click **Copy policy**, then:

11. Open **S3 > your bucket > Permissions > Bucket policy**, click **Edit**, paste the copied policy, and click **Save changes**.

Record the distribution's **ID** and **Domain name** (e.g. `d111111abcdef8.cloudfront.net`) from the distribution list — both go into Project Settings in step 10.

If your Addressables build uses `UnityWebRequest` from a domain other than the CDN host, also add a CORS configuration:

9. In S3, go to **your bucket > Permissions > Cross-origin resource sharing (CORS)**, click **Edit**, paste the following, and click **Save changes**:

```json
[{
  "AllowedOrigins": ["*"],
  "AllowedMethods": ["GET", "HEAD"],
  "AllowedHeaders": ["*"],
  "MaxAgeSeconds": 3000
}]
```

## 8. Create the IAM publisher user

This is the minimal-permission user whose credentials you share with Unity. It can only read/write/delete inside your bucket, trigger CloudFront invalidations, and deploy the cleanup Lambda stack — nothing else.

**Create the user and get its access key:**

1. In the AWS console, open **IAM > Users** and click **Create user**.
2. Enter a username (e.g. `vampire-therapist-publisher`) and click **Next**.
3. Leave permissions blank for now — click **Next → Create user**.
4. Open the newly created user, go to the **Security credentials** tab, and click **Create access key**.
5. Select **Command Line Interface (CLI)**, acknowledge the warning, and click **Next → Create access key**.
6. **Copy and save the Access Key ID and Secret Access Key** — the secret is shown only once.

**Attach the inline policy:**

7. Still on the user page, go to the **Permissions** tab and click **Add permissions → Create inline policy**.
8. Select the **JSON** tab, delete the placeholder, and paste the policy below.
9. Replace every `<placeholder>` with your actual value (the fixed names like `content-repo-cleanup-role` must stay exactly as shown — they are hardcoded in the CloudFormation template):

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": ["s3:ListBucket"],
      "Resource": "arn:aws:s3:::<bucket-name>"
    },
    {
      "Effect": "Allow",
      "Action": ["s3:GetObject","s3:PutObject","s3:DeleteObject"],
      "Resource": "arn:aws:s3:::<bucket-name>/*"
    },
    {
      "Effect": "Allow",
      "Action": ["cloudfront:CreateInvalidation","cloudfront:GetInvalidation"],
      "Resource": "arn:aws:cloudfront::<account-id>:distribution/<distribution-id>"
    },
    {
      "Effect": "Allow",
      "Action": ["cloudformation:GetTemplateSummary"],
      "Resource": "*"
    },
    {
      "Effect": "Allow",
      "Action": [
        "cloudformation:DescribeStacks",
        "cloudformation:CreateStack",
        "cloudformation:UpdateStack",
        "cloudformation:DeleteStack",
        "cloudformation:DescribeStackEvents",
        "cloudformation:DescribeStackResources",
        "cloudformation:DescribeChangeSet",
        "cloudformation:CreateChangeSet",
        "cloudformation:ExecuteChangeSet",
        "cloudformation:DeleteChangeSet"
      ],
      "Resource": "arn:aws:cloudformation:<region>:<account-id>:stack/content-repo-cleanup/*"
    },
    {
      "Effect": "Allow",
      "Action": [
        "iam:CreateRole","iam:DeleteRole","iam:GetRole","iam:PassRole",
        "iam:AttachRolePolicy","iam:DetachRolePolicy",
        "iam:PutRolePolicy","iam:DeleteRolePolicy","iam:GetRolePolicy","iam:TagRole"
      ],
      "Resource": "arn:aws:iam::<account-id>:role/content-repo-cleanup-role"
    },
    {
      "Effect": "Allow",
      "Action": [
        "lambda:CreateFunction","lambda:UpdateFunctionCode","lambda:UpdateFunctionConfiguration",
        "lambda:GetFunction","lambda:GetFunctionConfiguration","lambda:DeleteFunction",
        "lambda:AddPermission","lambda:RemovePermission","lambda:GetPolicy","lambda:TagResource"
      ],
      "Resource": "arn:aws:lambda:<region>:<account-id>:function:content-repo-cleanup"
    },
    {
      "Effect": "Allow",
      "Action": [
        "events:PutRule","events:DeleteRule","events:DescribeRule",
        "events:PutTargets","events:RemoveTargets","events:ListTargetsByRule"
      ],
      "Resource": "arn:aws:events:<region>:<account-id>:rule/content-repo-cleanup-daily"
    }
  ]
}
```

10. Click **Next**, name the policy `content-repo-publisher`, and click **Create policy**.

## 9. Configure credentials in Unity

1. Open **Project Settings > Content Repo > Upload**.
2. Fill in:
   - **S3 bucket name** — your bucket name from step 6
   - **S3 region** — e.g. `eu-central-1`
   - **CloudFront distribution ID** — from step 7
   - **CloudFront domain** — e.g. `d111111abcdef8.cloudfront.net`
3. Click **Configure credentials…** and enter the Access Key ID and Secret Access Key from step 8.
4. Click **Validate credentials** to confirm everything is wired up correctly.
   - On success: `✓ Credentials valid and bucket reachable.`
   - On failure: read the error in the console — common causes are missing AWS CLI on PATH, wrong region, or a typo in the bucket name.

> If you prefer to configure credentials on the command line instead: run `aws configure` and enter the publisher user's key and secret. The Unity button is a convenience wrapper around the same `aws configure set` commands.

---

## Troubleshooting

- **`aws` not on PATH** — restart Unity (and your terminal) after installing the CLI. On Windows, log out and back in if the new PATH isn't visible to the editor process.
- **`AccessDenied` on `s3 ls`** — the IAM policy is missing `s3:ListBucket` on the bucket ARN (without `/*`).
- **`InvalidClientTokenId`** — the access key was entered incorrectly or belongs to a deleted user. Run `aws sts get-caller-identity` to confirm which identity the CLI is currently using.
- **CloudFront still serves stale content** — the manifest path is invalidated automatically (`/<env>/manifest.json`); if you bypassed the editor flow you may need to run `aws cloudfront create-invalidation` manually.
