#!/bin/sh
# Run personally through a trusted interactive SSH terminal. No HTTP setup or account reset.
set -eu
[ "$(id -u)" = 0 ] || { echo 'Run as root on the production host.' >&2; exit 1; }
[ -t 0 ] && [ -t 1 ] || { echo 'An interactive private terminal is required.' >&2; exit 1; }
exec 9>/run/lock/pidock-storage-production.lock
flock -n 9 || { echo 'A storage maintenance operation is already running.' >&2; exit 1; }
cloud=pidock-production-cloud
setup=pidock-production-commission
[ "$(docker inspect --format '{{ index .Config.Labels "io.pidock.environment" }}' "$cloud")" = production ]
if docker inspect "$setup" >/dev/null 2>&1; then
    echo 'A commissioning session already exists. Review it locally before retrying.' >&2
    exit 1
fi
image=$(docker inspect --format '{{.Image}}' "$cloud")
cleanup() {
    docker rm --force "$setup" >/dev/null 2>&1 || true
    docker start "$cloud" >/dev/null
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP
docker stop --time 20 "$cloud" >/dev/null
printf '%s\n' 'Choose your own cloud username/password. Use tenant local and role Owner.'
printf '%s\n' 'Add the displayed setup secret to your external authenticator, then enter two consecutive generated codes.'
docker run --rm -it --name "$setup" --log-driver none --network none --read-only \
    --user 1654:1654 --cap-drop ALL --security-opt no-new-privileges:true \
    --pids-limit 128 --memory 512m --tmpfs /tmp:rw,noexec,nosuid,size=16m \
    --env ASPNETCORE_ENVIRONMENT=Production --env PiDock__StateDirectory=/var/lib/pidock \
    --mount type=volume,source=pidock-production-cloud,target=/var/lib/pidock,volume-nocopy \
    "$image" --commission
