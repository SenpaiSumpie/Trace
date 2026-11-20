# Build context: the repository root.
FROM node:24-alpine AS build
WORKDIR /web
COPY apps/web/package.json apps/web/package-lock.json ./
RUN npm ci
COPY apps/web/ ./
RUN npm run build

# Serves the static app and forwards /api and /health to the API, so the browser only ever
# talks to one origin (which is what makes the session cookie first-party).
FROM nginxinc/nginx-unprivileged:stable-alpine
COPY infra/nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /web/dist /usr/share/nginx/html
EXPOSE 8080
