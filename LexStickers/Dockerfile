FROM node:22-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && useradd -m -u 1000 user

WORKDIR /app

COPY --chown=user:user package.json ./
COPY --chown=user:user server/package.json server/
COPY --chown=user:user web/package.json web/

RUN npm --prefix server install --omit=dev \
    && npm --prefix web install \
    && npm cache clean --force

COPY --chown=user:user server/src ./server/src
COPY --chown=user:user web ./web

RUN npm --prefix web run build \
    && rm -rf /app/web/node_modules /root/.npm \
    && mkdir -p /app/server/data/sources /app/server/data/outputs /app/server/data/auth_sessions \
    && chown -R user:user /app

USER user

ENV NODE_OPTIONS=--max-old-space-size=384
ENV PORT=10000
EXPOSE 10000

CMD ["node", "server/src/index.js"]
