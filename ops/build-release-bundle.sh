#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"

release_tag=""
owner="${GITHUB_REPOSITORY_OWNER:-przemekp95}"
output_root="${repo_root}/output/release"

while (($# > 0)); do
  case "$1" in
    --release-tag)
      release_tag="$2"
      shift 2
      ;;
    --owner)
      owner="$2"
      shift 2
      ;;
    --output-root)
      output_root="$2"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

if [[ -z "$release_tag" ]]; then
  echo "Release tag is required. Pass --release-tag vX.Y.Z." >&2
  exit 1
fi

bundle_root="${output_root}/lawwatcher-${release_tag}"
rm -rf "$bundle_root"
mkdir -p "$bundle_root/compose" "$bundle_root/env" "$bundle_root/docs"

cp "${repo_root}/ops/compose/docker-compose.yml" "$bundle_root/compose/"
cp "${repo_root}/ops/compose/docker-compose.production.yml" "$bundle_root/compose/"
cp "${repo_root}/ops/compose/hybrid-runtime-preflight.sh" "$bundle_root/compose/"
cp "${repo_root}/ops/run-docker-production.sh" "$bundle_root/"
cp "${repo_root}/ops/stop-docker-production.sh" "$bundle_root/"
cp "${repo_root}/ops/validate-production-env.sh" "$bundle_root/"

template_env="${bundle_root}/env/production.env"
sed \
  -e "s|ghcr.io/przemekp95/|ghcr.io/${owner}/|g" \
  -e "s|:v1.0.0|:${release_tag}|g" \
  "${repo_root}/ops/env/production.env.example" > "$template_env"

cp "${repo_root}/docs/INSTALL.md" "$bundle_root/docs/"
cp "${repo_root}/docs/CONFIGURATION.md" "$bundle_root/docs/"
cp "${repo_root}/docs/BACKUP-RESTORE.md" "$bundle_root/docs/"
cp "${repo_root}/docs/UPGRADES.md" "$bundle_root/docs/"
cp "${repo_root}/docs/SUPPORT.md" "$bundle_root/docs/"

cat > "$bundle_root/manifest.json" <<EOF
{
  "product": "LawWatcher",
  "releaseTag": "${release_tag}",
  "imageOwner": "${owner}",
  "distribution": "private-self-hosted-ghcr-compose",
  "supportedRuntime": "production",
  "includedServices": [
    "api",
    "portal",
    "worker-ai",
    "worker-documents",
    "worker-projection",
    "worker-notifications",
    "worker-replay",
    "sqlserver",
    "rabbitmq",
    "minio",
    "ollama",
    "opensearch"
  ]
}
EOF

(
  cd "$bundle_root"
  find . -type f | sort | while read -r path; do
    sha256sum "$path"
  done > SHA256SUMS
)

archive_path="${output_root}/lawwatcher-${release_tag}.tar.gz"
tar -czf "$archive_path" -C "$output_root" "lawwatcher-${release_tag}"

echo "Release bundle prepared:"
echo "  Directory: $bundle_root"
echo "  Archive:   $archive_path"
