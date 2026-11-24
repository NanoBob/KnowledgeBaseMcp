FROM node:25-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .

# The API is a separate application, so point the build at its absolute URL. Leave empty to
# call the API on the page's own origin, which is what the code assumes by default.
ARG VITE_API_BASE=
ENV VITE_API_BASE=${VITE_API_BASE}

RUN npm run build

FROM nginxinc/nginx-unprivileged:alpine AS final
# The runtime API base comes from API_BASE; the build-time VITE_API_BASE stays as a fallback.
COPY --chmod=755 config-entrypoint.sh /docker-entrypoint.d/40-app-config.sh
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
