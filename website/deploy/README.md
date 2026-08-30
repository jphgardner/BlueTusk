# BlueTusk website deployment

The production website runs as three unprivileged, read-only Nginx replicas behind three Traefik replicas and one DigitalOcean regional load balancer. Website and Traefik containers are pinned by immutable digest. cert-manager obtains and renews the public ECDSA certificate from Let's Encrypt through Traefik HTTP-01 challenges.

## Release

1. Run `npm ci`, `npm test -- --watch=false`, and `npm run build` in `website`.
2. Build and push the image from the `website` directory context.
3. Replace the deployment image with the registry digest, never a mutable tag.
4. Reconcile cert-manager `v1.21.1` with `deploy/cert-manager-values.yaml`.
5. Reconcile Traefik chart `41.4.0` with `deploy/traefik-values.yaml`. Its chart Service stays disabled because the existing `bluetusk-website` Service is the sole public load balancer.
6. Apply `deploy/kubernetes.yaml`, `deploy/ingress.yaml`, and `deploy/certificate.yaml`.
7. Wait for the website, Traefik, ClusterIssuer, and Certificate readiness conditions.
8. Verify HTTP-to-HTTPS redirect, `/health/ready`, `/`, a documentation deep link, response security headers, the certificate chain, and three replicas of both data-plane deployments.

## Operations

- Readiness: `/health/ready`
- Liveness: `/health/live`
- Public URL: `https://bluetusk.io`
- Certificate: `kubectl get certificate/bluetusk-io-v2 -n bluetusk-web`
- Traefik: `kubectl rollout status deployment/traefik -n bluetusk-web`
- Rollout: `kubectl rollout status deployment/bluetusk-website -n bluetusk-web`
- History: `kubectl rollout history deployment/bluetusk-website -n bluetusk-web`
- Rollback: `kubectl rollout undo deployment/bluetusk-website -n bluetusk-web`

## Domain

The DigitalOcean DNS zone for `bluetusk.io` contains an apex A record for the website load balancer and a `www` CNAME to the apex. Delegate the domain at its registrar to all three authoritative name servers:

- `ns1.digitalocean.com`
- `ns2.digitalocean.com`
- `ns3.digitalocean.com`

The live zone maps the apex to the sole load balancer, maps `www` to the apex, and preserves the existing DMARC policy. cert-manager renews the certificate 30 days before expiry. Traefik permanently redirects HTTP to HTTPS after issuance while its higher-priority ACME challenge route remains available for renewal.
