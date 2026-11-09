#!/usr/bin/env bash
set -euo pipefail
export MSYS_NO_PATHCONV=1

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
target="${1:-}"
action="${2:-}"

usage() {
    echo "usage: bash deploy/demo/demo.sh <dev|prod> <plan|seed|advance [until]|unseed|counts>" >&2
    exit 1
}

case "$target" in
    dev) compose_file="docker-compose.yml"; ahead="${AHEAD_DAYS:-0}" ;;
    prod) compose_file="docker-compose.prod.yml"; ahead="${AHEAD_DAYS:-365}" ;;
    *) usage ;;
esac

folder="$repo/data/$target/demo"
mkdir -p "$folder"

windows() {
    if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi
}

compose() {
    docker compose -f "$(windows "$repo/deploy/compose/$compose_file")" "$@"
}

python_bin() {
    for candidate in "$repo/src/caligo-ml/.venv/Scripts/python.exe" "$repo/src/caligo-ml/.venv/bin/python"; do
        [ -x "$candidate" ] && { echo "$candidate"; return; }
    done
    echo "no virtual environment in src/caligo-ml/.venv" >&2
    exit 1
}

counts() {
    compose exec -T postgres sh -c 'psql -U "${POSTGRES_USER:-postgres}" -d caligodb -At -F " | "' <<'SQL' | grep -v '^SET$'
SET search_path TO streamingservice, public;
SELECT 'identity users', count(*) FROM public."AspNetUsers"
UNION ALL SELECT 'identity roles', count(*) FROM public."AspNetUserRoles"
UNION ALL SELECT 'demo identity users', count(*) FROM public."AspNetUsers" WHERE "NormalizedEmail" LIKE '%@DEMO.INVALID'
UNION ALL SELECT 'profiles', count(*) FROM "user"
UNION ALL SELECT 'bloguser', count(*) FROM bloguser
UNION ALL SELECT 'payments', count(*) FROM payments
UNION ALL SELECT 'watchhistory', count(*) FROM watchhistory
UNION ALL SELECT 'interactions', count(*) FROM usercontentinteraction
UNION ALL SELECT 'comments', count(*) FROM comment
UNION ALL SELECT 'collections', count(*) FROM collection
UNION ALL SELECT 'collection titles', count(*) FROM collectiontitle
UNION ALL SELECT 'notifications', count(*) FROM notifications
UNION ALL SELECT 'recommendations', count(*) FROM currentcontentrecommendations
UNION ALL SELECT 'playback sessions', count(*) FROM playbacksession
UNION ALL SELECT 'playback positions', count(*) FROM playbackposition;
SELECT 'averages md5', md5(string_agg(content_id || ':' || coalesce(average_rating::text, 'null'), ',' ORDER BY content_id)) FROM content;
SELECT 'watch md5', md5(string_agg("watched-content_id" || ':' || content_id || ':' || user_id || ':' || date, ',' ORDER BY "watched-content_id")) FROM watchhistory;
SELECT 'comment md5', md5(string_agg(comment_id || ':' || user_id || ':' || content_id || ':' || comment, ',' ORDER BY comment_id)) FROM comment;
SQL
}

compare() {
    paste -d'|' "$1" "$2" | awk -F'|' '{ mark = ($2 == $4) ? "" : "  changed"; printf "%-22s %34s  %-34s%s\n", $1, $2, $4, mark }'
}

api() {
    if [ "$target" = prod ]; then
        compose run --rm --no-deps caligo-api "$@"
        return
    fi
    local dll="$repo/src/posts/Caligo.Api/bin/Debug/net8.0/Caligo.Api.dll"
    [ -f "$dll" ] || { echo "build the API first: dotnet build src/posts/Caligo.Api" >&2; exit 1; }
    (
        cd "$repo/src/posts/Caligo.Api"
        export ASPNETCORE_ENVIRONMENT=Development
        export ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=caligodb;Username=postgres;Password=postgres"
        export Database__FallbackToSqlite=false
        export Jwt__Key=0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ Jwt__Issuer=caligo-api Jwt__Audience=caligo-client
        export AdminUser__Email=admin@localhost.dev AdminUser__Password='AdminDev04!'
        export Logging__LogLevel__Default=Warning
        dotnet "$(windows "$dll")" "$@"
    )
}

file_for_api() {
    if [ "$target" = prod ]; then echo "/demo/$1"; else windows "$folder/$1"; fi
}

case "$action" in
    plan)
        for file in catalog.json demo_stats.json; do
            [ -f "$folder/$file" ] || cp "$repo/data/dev/demo/$file" "$folder/$file"
        done
        python="$(python_bin)"
        (
            cd "$repo/src/caligo-ml"
            "$python" -m caligo_ml.demo.generate \
                --catalog "$(windows "$folder/catalog.json")" --stats "$(windows "$folder/demo_stats.json")" \
                --models "$(windows "$repo/data/$target/ml")" --out "$(windows "$folder/plan.json")" \
                --end "$(date -u +%Y-%m-%dT%H:00:00)" --ahead-days "$ahead" > "$folder/plan-report.json"
        )
        echo "plan written to data/$target/demo/plan.json, report in plan-report.json"
        ;;
    seed)
        counts > "$folder/counts-before.txt"
        api seed-demo "$(file_for_api plan.json)"
        counts > "$folder/counts-seeded.txt"
        compare "$folder/counts-before.txt" "$folder/counts-seeded.txt"
        ;;
    advance)
        api advance-demo "$(file_for_api plan.json)" ${3:+"$3"}
        ;;
    unseed)
        api unseed-demo "$(file_for_api seeded.json)"
        counts > "$folder/counts-unseeded.txt"
        [ -f "$folder/counts-before.txt" ] && compare "$folder/counts-before.txt" "$folder/counts-unseeded.txt"
        ;;
    counts)
        counts
        ;;
    *)
        usage
        ;;
esac
