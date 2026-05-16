#!/usr/bin/env bash
# BabyBrain deploy script. Invoked by the GitHub Actions workflow over SSH;
# the key in authorized_keys forces this exact command, so the workflow's
# `script:` value is preserved in $SSH_ORIGINAL_COMMAND. We parse that for
# KEY=VALUE tokens and write them to .env.deploy-override, which docker-compose
# loads as an env_file on top of .env. Adding a new per-deploy override
# variable is a one-line change in the workflow YAML — this script never
# needs to know about specific variables.
#
# Lives at /opt/babybrain/scripts/deploy.sh (tracked in git). bash reads the
# file into memory at exec time, so a `git pull` that updates this script
# mid-run is safe: the current invocation finishes with the old version, the
# next deploy picks up the new one.
set -euo pipefail
cd /opt/babybrain
git pull --ff-only

# Regenerate the override file fresh each run so a previous deploy's overrides
# don't leak forward. Empty file is fine — compose loads it with no effect.
: > .env.deploy-override
for arg in ${SSH_ORIGINAL_COMMAND:-}; do
    # Only canonical UPPER_CASE env-var-shaped tokens with a NON-EMPTY value.
    # Anything else — a stray path, an option flag, an injection attempt, or a
    # bare `KEY=` produced by a GitHub secret that isn't set — is silently
    # dropped. Dropping `KEY=` is deliberate: an absent secret must not clobber
    # the value already in .env on the box.
    [[ "$arg" =~ ^[A-Z_][A-Z0-9_]*=.+$ ]] || continue
    echo "$arg" >> .env.deploy-override
    # Log the key only — never the value, since these can be secrets.
    echo "deploy: override ${arg%%=*}"
done

docker compose up -d --build
docker image prune -f
