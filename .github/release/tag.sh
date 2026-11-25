# Shared by release preflight and publication.
require_release_tag() {
  local tag=$1 expected=$2 refs commit
  local version='v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?'
  if [[ ! "$tag" =~ ^$version$ ]]; then
    echo 'Expected vMAJOR.MINOR.PATCH with an optional prerelease suffix.' >&2
    return 1
  fi

  refs=$(git ls-remote --exit-code --tags origin "refs/tags/$tag" "refs/tags/$tag^{}") || return
  commit=$(awk -v ref="refs/tags/$tag" '
    $2 == ref { direct = $1 }
    $2 == ref "^{}" { peeled = $1 }
    END { print peeled ? peeled : direct }
  ' <<< "$refs")
  if [[ "$commit" != "$expected" ]]; then
    echo 'Release tag does not resolve to the dispatch commit.' >&2
    return 1
  fi
}
