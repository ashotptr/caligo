FROM postgres:16-alpine

COPY --chmod=644 init-databases.sh /docker-entrypoint-initdb.d/init-databases.sh
