FROM node:24-bookworm-slim
ENV NODE_ENV=production
WORKDIR /app/portal
COPY portal/package.json portal/package-lock.json ./
RUN npm ci --omit=dev
COPY portal/ ./
COPY Assets/StreamingAssets/lessons/ /app/Assets/StreamingAssets/lessons/
COPY scene-openings/ /app/scene-openings/
CMD ["sh", "deploy/start.sh"]
