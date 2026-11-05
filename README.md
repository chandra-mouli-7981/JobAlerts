# Job Alert Agent

Checks company career sites every morning (08:00 IST) and **emails you the new openings** that match
your keywords, using Gmail. No LLM involved: plain fetching, filtering and de-duplication.

```
GitHub Actions cron -> .NET console app -> provider adapters -> keyword filter -> seen-jobs store -> Gmail
```

## One-time setup (do this on your personal laptop / personal GitHub account)

1. **Get a Gmail app password.** Google account > Security > turn on **2-Step Verification**, then open
   <https://myaccount.google.com/apppasswords>, create an app password (any name) and copy the 16 characters.
   Use a personal Gmail account, not a work one.
2. **Create an empty GitHub repository** and upload the contents of this folder, **including the hidden
   `.github` folder** (it holds the daily schedule). Unzip first; GitHub's website cannot unpack zips.
3. **Add repository secrets** (Settings > Secrets and variables > Actions > New repository secret):

   | Secret               | Value                                                     | Required |
   |----------------------|-----------------------------------------------------------|----------|
   | `GMAIL_ADDRESS`      | your Gmail address (used to log in and as sender)         | yes      |
   | `GMAIL_APP_PASSWORD` | the 16-character app password (spaces are ignored)        | yes      |
   | `MAIL_TO`            | where to send the digest; leave unset to email yourself   | no       |

4. **Optional local check** (PowerShell, from this folder):
   ```powershell
   $env:GMAIL_ADDRESS = "you@gmail.com"; $env:GMAIL_APP_PASSWORD = "xxxx xxxx xxxx xxxx"
   dotnet run --project src/JobAlertAgent -- --test-email
   ```
5. In GitHub: Actions > **Daily job alert** > Run workflow. The first run does not flood your inbox: it indexes
   all currently matching openings and sends one "set up" email. From the next run you only get new openings.
   (Tick `initial_digest` to receive the full list on the first run instead.)

After that it runs by itself every day. The workflow commits `data/seen-jobs.json` back to the repo; those
commits also keep the repo "active" so GitHub does not disable the schedule after 60 days of inactivity.

If the email does not show up, check the Spam folder once and mark it "Not spam".

## Adding a company or keyword (easy)

Edit `config/jobalert.config.json` - on GitHub's website or mobile app (pencil icon > edit > commit).
The file starts with a comment block that explains this too.

**Add a company: paste one line into `"sources"`**, using a link to its job board:

```json
"sources": [
  "https://jobs.lever.co/acme",
  "https://boards.greenhouse.io/acme",
  "https://jobs.ashbyhq.com/acme",
  "https://apply.workable.com/acme",
  "https://acme.wd5.myworkdayjobs.com/SiteName",
  "lever:acme"
]
```

- Supported: Greenhouse, Lever, Ashby, Workable and Workday boards (the agent works out the company from the link),
  `amazon.jobs` and `apply.careers.microsoft.com`. Shorthand `greenhouse:acme`, `lever:acme`, `ashby:acme`, `workable:acme` also works.
- Not sure which system a company uses? Open its careers page and click **Apply** on any job: the address
  usually shows `lever.co`, `greenhouse.io`, `ashbyhq.com` or `workable.com` followed by the company name. Paste that link.
  For Workday (`myworkdayjobs.com`), paste the address of the company's job list, e.g. `https://nvidia.wd5.myworkdayjobs.com/NVIDIAExternalCareerSite`.
  Workday gives no job descriptions, so for Workday companies keywords match the **job title** only.
- Every line except the last in a list needs a comma after it.
- The name in the email is taken from the link. To choose it: `{ "name": "Acme Corp", "url": "https://jobs.lever.co/acme" }`.
  Changing a name later makes that company's jobs look new once.
- Pause a company: `{ "url": "https://jobs.lever.co/acme", "enabled": false }`.
- **Check a new line works:** run the workflow with **dry_run** ticked and read the log: `[ok]` or `[FAIL]` per company.
  A wrong or unsupported line never stops the other companies; it shows up in your email under "Could not check".
- **Only India jobs from a global company:** `{ "name": "Okta", "url": "https://job-boards.greenhouse.io/okta", "locations": ["India"] }`.
  "India" also covers Bengaluru, Hyderabad, Pune, Mumbai, Gurugram, Chennai, Noida, Delhi ...; any other place (e.g. `"London"`) can be listed too.
- Amazon / Microsoft are searches and can be narrowed:
  `{ "url": "https://www.amazon.jobs", "countryCodes": ["IND"] }`, `{ "url": "https://apply.careers.microsoft.com", "location": "India" }`.

**Add a keyword:** add a line to `"keywords"`, e.g. `"Kubernetes",`. A job matches if **any** keyword matches
(case-insensitive, whole words: `React` does not match `Reaction`; `.NET` matches `ASP.NET`).
To match the job title only (less noise): `{ "term": "Azure", "scope": "title" }`.
Shipped keywords: Applied AI Engineer, Applied Engineer, .NET, React, Angular (title or description) and
Azure, Python, SQL, AWS, SRE, DevOps, Site Reliability, Gen AI, GenAI, Generative AI (title only, because they appear in the boilerplate of many unrelated postings).

`seenRetentionDays`: how long an unseen job stays in the state file.

Advanced: an entry can still use `provider` (`greenhouse`, `lever`, `ashby`, `workable`, `workday`, `amazon`, `microsoft`) and `slug`
instead of `url`.

## Running locally

```powershell
dotnet test JobAlertAgent.sln
dotnet run --project src/JobAlertAgent -- --dry-run      # prints the digest, sends nothing, writes nothing
```

Options: `--config`, `--state`, `--dry-run`, `--initial-digest`, `--test-email`.
Exit codes: `0` ok (individual source failures are listed in the email), `1` every source failed or the
run failed, `2` configuration error.

## Caveats

- These career endpoints are public but unofficial; a provider can change its format without notice.
  A broken source is reported in the email instead of failing silently.
- GitHub may start scheduled runs a few minutes late.

