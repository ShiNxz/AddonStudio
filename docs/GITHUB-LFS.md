# Putting an addon on GitHub (with Git LFS)

Each addon is its own git repo. Text sources (xml, css, js, vmat, vmdl, vpcf, vsndevts, workshop.json) go into normal git. Big binary files (png, tga, psd, wav, mp3, fbx, dmx, everything in `prebuilt/`) go through Git LFS, so the repo stays fast and GitHub's 100 MB per-file limit doesn't apply to them.

Addon Studio writes the `.gitattributes` with the LFS rules when it creates or imports a project, so you only set up the account side once.

## Costs (GitHub Free / Pro)

- 10 GiB of LFS storage and 10 GiB of LFS downloads per month are free.
- After that: about $0.07 per GiB per month for storage and $0.0875 per GiB downloaded.
- Every version of a binary you ever pushed counts toward storage, not just the latest one.
- Without a payment method on the account, pushes are blocked once you hit the free quota. Add one at github.com > Settings > Billing and licensing > Payment information if your addons are bigger than ~10 GiB in total.
- Example: three addons of 3 GB each plus some history is around 12 to 20 GiB, so roughly $0.15 to $0.70 a month.

Usage is at github.com > Settings > Billing and licensing > Usage (filter by Git LFS).

## One-time setup on the PC

1. Install Git for Windows from https://git-scm.com/download/win. It includes Git LFS. Keep "Git Credential Manager" selected in the installer.
2. Open a terminal and run:

   ```
   git lfs install
   ```

3. For the one-click "Create private GitHub repo" button in Addon Studio, install the GitHub CLI from https://cli.github.com and log in once:

   ```
   gh auth login
   ```

   Pick GitHub.com, HTTPS, and log in with the browser.

## Putting a project on GitHub

From the app: open the project, Changes tab, "Create private GitHub repo". It creates the repo, sets it as `origin` and pushes everything, LFS files included.

By hand (same result):

1. Create an empty private repo on github.com (no README, no .gitignore).
2. In the project folder:

   ```
   git remote add origin https://github.com/<you>/<repo>.git
   git push -u origin main
   ```

The first push uploads all LFS files, so it can take a while for a 3 GB addon.

## Cloning on another PC

In Addon Studio: Add project > Clone, paste the repo URL. It runs `git clone` and then `git lfs pull`. By hand:

```
git clone https://github.com/<you>/<repo>.git
cd <repo>
git lfs pull
```

## Already have big files committed without LFS?

If a repo was created before LFS was set up, move the existing big files into LFS (this rewrites history, so do it before anyone else clones the repo):

```
git lfs migrate import --include="*.png,*.jpg,*.jpeg,*.tga,*.psd,*.exr,*.wav,*.mp3,*.fbx,*.dmx,*.obj,*.vpk,prebuilt/**" --everything
git push --force-with-lease --all
```

## Checking it works

```
git lfs ls-files
```

lists the files stored in LFS. A png or a prebuilt model missing from that list means it went into plain git; check `.gitattributes`.
