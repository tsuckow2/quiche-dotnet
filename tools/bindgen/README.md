# Regenerating the P/Invoke layer

`src/Quiche.NET/Native/NativeMethods.g.cs` is generated, not written
by hand. It comes from quiche's own Rust FFI source
(`quiche/src/ffi.rs`) via [csbindgen](https://github.com/Cysharp/CsBindgen)
(the same tool whose repo carries a quiche showcase binding).

## Regenerate

```sh
# 1. Check out quiche at the pinned version (see native/QUICHE_VERSION).
git clone --branch <tag> https://github.com/cloudflare/quiche.git /tmp/quiche

# 2. Run the generator (this project; needs a Rust toolchain, but
#    only at generation time — never at package consume time).
cargo run --manifest-path tools/bindgen/Cargo.toml -- \
    /tmp/quiche src/Quiche.NET/Native/NativeMethods.g.cs

# 3. Build and diff-review: the generated file should change only
#    where quiche's C API changed.
dotnet build src/Quiche.NET/Quiche.NET.csproj -c Release
git diff src/Quiche.NET/Native/NativeMethods.g.cs
```

## Why the generator has opinions

Two quirks are baked into `tools/bindgen/src/main.rs` so the output
compiles cleanly; do not "fix" them by hand-editing the generated
file (the next regen would drop your fix):

- **Extern, not bindgen, input.** csbindgen's bindgen-file mode
  collects `extern "C" { ... }` foreign declarations (for quiche's
  ffi.rs that yields only `inet_ntop`). quiche's API is
  `#[no_mangle] pub extern "C" fn` definitions, which only the
  extern collector emits.
- **Method filter.** `ffi.rs` defines
  `quiche_conn_new_with_tls_and_client_dcid` twice (cfg-gated on
  the `custom-client-dcid` feature, which our builds do not enable).
  The filter drops it; the managed API never needs custom client
  DCIDs. If a future step wants it, enable the feature in the
  native build first, then drop the filter.
- **Type renames.** A few signatures spell `size_t`/`ssize_t`
  where the rest use `libc::size_t` (already mapped to
  `nuint`/`nint`); the renames normalize the stragglers.

## What csbindgen does NOT emit (hand-written companions)

- `src/Quiche.NET/Native/NativeTypes.cs`: types the generated
  signatures reference but that live outside ffi.rs (opaque
  `Config`/`Connection`/iterators; libc `sockaddr`,
  `sockaddr_storage`, `timespec`).
- `src/Quiche.NET/Native/NativeErrors.cs`: quiche's error codes.
  quiche's Rust `Error` enum is not a repr(C) type in ffi.rs, so
  the codes exist only as negative `ssize_t` returns; they are
  spelled out from `quiche/include/quiche.h`.

## On a quiche bump

1. Update `native/QUICHE_VERSION` (tag AND commit).
2. Re-run the generator above and review the diff.
3. Re-check the hand-written companions against the new
   `quiche.h`: new referenced-but-undeclared types go in
   `NativeTypes.cs`; changed error codes go in `NativeErrors.cs`.
4. Add a CHANGELOG entry (see CHANGELOG.md once step 5 lands it).
