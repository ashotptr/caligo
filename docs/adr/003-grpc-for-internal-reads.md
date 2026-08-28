# 003 - gRPC for internal reads

**Status:** accepted

## Context

Once analytics moved out, the API needed view counts from it on every post render: internal,
synchronous, high frequency, and on the critical path of a page the reader is waiting for.

REST over JSON would work. The question is what gRPC buys for the extra build-time machinery.

## Decision

gRPC, with the `.proto` compiled once into a shared `Blog.Contracts.Grpc` project that both
sides reference.

## Consequences

**What it buys.**

- A contract the compiler checks on both sides. A field renamed in the proto breaks the build,
  not a deserialiser at runtime.
- Protobuf over HTTP/2 on a reused connection: smaller payloads, no per-call handshake.
- **Server streaming**, which is what feeds the live trending panel. The REST alternative is
  every dashboard polling on a timer, paying a request per client per interval whether or not
  anything changed - and still showing stale data in between.
- A batch method, because a listing needs counts for twenty posts. Twenty unary calls to render
  one page is the N+1 problem wearing a different hat.

**What it costs.** Codegen in the build. Browsers cannot consume a gRPC stream, so the API
terminates `StreamTrending` and re-emits it as Server-Sent Events - one extra hop, and the
reason the ingress needs `proxy-buffering: off`.

**A mistake worth recording.** The proto was first compiled separately in each service - client
half in one, server half in the other. That builds fine and produces *two distinct CLR types
with the same full name*, which collides the moment anything references both. Generating once
into a shared project is what actually delivers the guarantee the contract is for.
