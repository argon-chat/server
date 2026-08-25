# The installer, and afterwards the panel — one image, two jobs (design §10).
#
#   docker run --rm -v /opt/argon:/argon <image> emit-bootstrap --domain=… --traffic=…
#   docker run -v /opt/argon:/argon -v /var/run/docker.sock:/var/run/docker.sock <image>
#
# The first writes the front door's compose project and exits; the second serves the setup UI and then
# keeps serving as the control surface. Which one runs is the first argument, and nothing else differs.

FROM oven/bun:1.4-alpine AS dependencies

WORKDIR /app

# Dependencies first, so that editing a source file does not re-resolve the tree. `--frozen-lockfile`
# because an installer that quietly floats a dependency is an installer whose behaviour depends on the
# day it was built.
#
# Not `--production` here: this layer feeds the build below, and Vue's compiler is a devDependency. The
# runtime layer takes only what the second install resolves.
COPY package.json bun.lock ./
RUN bun install --frozen-lockfile


FROM dependencies AS page

WORKDIR /app

# The Vue application. Built here rather than shipped as sources, because the runtime image has no
# bundler in it and should not: what serves the page is three files by name — see `server.ts` — and a
# container that could build its own front end could also be talked into building something else.
COPY ui ./ui
RUN cd ui && bunx vite build


FROM oven/bun:1.4-alpine AS runtime

WORKDIR /app

# Resolved again, this time without the build's tools. Vue, Vite and the Playwright driver have no
# business in an image that faces an operator's machine.
COPY package.json bun.lock ./
RUN bun install --frozen-lockfile --production

COPY package.json tsconfig.json ./
COPY src ./src

# Only what the build emitted: index.html, app.js, app.css. `server.ts` resolves this relative to
# itself, so it has to sit beside src/ — and the sources it was built from are deliberately absent.
COPY --from=page /app/ui/dist ./ui/dist

# Where the install root is mounted. `config.ts` defaults to this; the compose project mounts the host's
# install directory here and the two have to agree.
ENV ARGON_BOOTSTRAP_CONFIG_DIR=/argon

# Root, and deliberately. This process reads the docker socket — §10 calls that root-equivalent and
# accepts it, because the panel's whole job is starting and stopping containers — and it writes files
# into a bind-mounted install root whose ownership belongs to the host, not to a uid invented here.
# Dropping to a non-root user would buy nothing against a process that can already create privileged
# containers, and would cost the ability to write the configuration it exists to write.
USER root

# No CMD. Given none, this serves; given `emit-bootstrap …`, it emits. A default CMD would make the
# difference between the two a thing you can get wrong by forgetting to override it.
ENTRYPOINT ["bun", "run", "/app/src/main.ts"]
