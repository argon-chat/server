# Why TypeScript 6 and not 7

`typescript` is held at `~6.0.3` deliberately, and moving it forward breaks something that will not
announce itself.

TypeScript 7 is the native compiler, and it no longer exports the `typescript/lib/tsc` subpath. `vue-tsc`
resolves exactly that path, so under 7 it throws `ERR_PACKAGE_PATH_NOT_EXPORTED` before reading a single
file. The failure looks like a broken command rather than like a missing check, which is the dangerous
part: the obvious response is to drop `vue-tsc` from the pipeline and carry on with a green `tsc`.

What that would cost is every expression inside a `<template>`. `tsc` alone does not read `.vue` files at
all — with a `declare module "*.vue"` shim it treats each component as opaque, so `{{ row.verdcit }}`
compiles, renders nothing, and reaches an operator. Worse, the shim flattens component props to
`Record<string, unknown>`, so passing `:reprots` instead of `:reports` between two components is silently
fine. Both were verified by introducing exactly those two mistakes: under `vue-tsc` on TypeScript 6 both
are caught; with the shim in place the prop error is not.

So the ordering is: `vue-tsc` is the check that matters for this package, `vue-tsc` needs TypeScript 6,
and TypeScript 6 is therefore the constraint rather than the compromise. Revisit when `vue-tsc` supports
the native compiler — the marker is that `bunx vue-tsc --noEmit -p ui/tsconfig.json` runs under 7.

There is no `shims-vue.d.ts` in this repository, and adding one back would undo the above.
