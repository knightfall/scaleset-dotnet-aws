#!/bin/sh
set -eu

name=${0##*/}
case "$name" in
    Runner.Worker|Runner.PluginHost) ;;
    *) exit 126 ;;
esac

exec "$SCALESET_RUNNER_DOTNET" exec "$SCALESET_RUNNER_ROOT/bin/$name.dll" "$@"
