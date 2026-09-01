/// <reference types="vite/client" />

// A stylesheet imported for its side effect has no shape to describe, and @argon/assets exports it
// through its own export map, where TypeScript looks for a type and finds none.
declare module "@argon/assets/styles";
