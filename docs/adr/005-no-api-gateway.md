# 005 - No API gateway

**Status:** accepted

## Context

An API gateway appears on most microservice checklists, and YARP is the obvious .NET choice.
With three services behind one origin, adding one would be unremarkable.

## Decision

No gateway. nginx already terminates TLS, serves the SPA's static assets, and routes `/api/`;
in Kubernetes the ingress controller does the same job.

## Consequences

**What a gateway would have added here.** Centralised JWT validation - but each service already
validates its own token, and a gateway that validated on their behalf would mean a service is
only safe as long as nothing reaches it directly. Rate limiting - already implemented per
service, where the limits can differ by endpoint. Request aggregation - the one composition in
the system (counts from analytics joined with titles from the posts database) needs the posts
database, so it belongs in the posts service, not in a gateway that would need its own copy.

**What it would have cost.** Another hop on every request, another deployment, another thing to
be down.

**What would change this.** A second public client with different aggregation needs, auth that
needs central token exchange, or enough services that per-service edge configuration becomes
duplication rather than locality. At three services none of that is true.

Declining to add a component is a decision, and this file is where it is recorded rather than
left as a gap someone assumes was an oversight.
