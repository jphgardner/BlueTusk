using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace BlueTusk.ControlPlane.Tests;

public sealed class KubernetesTopologyGraphBuilderTests
{
    [Fact]
    public void BuildsLiveTopologyFromKubernetesIdentityAndRelationships()
    {
        var resources = ParseResources(
            """
            {
              "metadata": {},
              "items": [
                {
                  "apiVersion": "apps/v1",
                  "kind": "Deployment",
                  "metadata": {
                    "namespace": "bluetusk-web",
                    "name": "dashboard",
                    "uid": "deployment-uid",
                    "resourceVersion": "101",
                    "labels": { "app": "dashboard" }
                  },
                  "spec": {
                    "replicas": 2,
                    "template": {
                      "metadata": { "labels": { "app": "dashboard" } },
                      "spec": {
                        "containers": [
                          { "name": "dashboard", "image": "registry.example/dashboard@sha256:abc" }
                        ]
                      }
                    }
                  },
                  "status": { "availableReplicas": 2 }
                },
                {
                  "apiVersion": "apps/v1",
                  "kind": "ReplicaSet",
                  "metadata": {
                    "namespace": "bluetusk-web",
                    "name": "dashboard-abc",
                    "uid": "replicaset-uid",
                    "resourceVersion": "102",
                    "ownerReferences": [
                      { "kind": "Deployment", "name": "dashboard", "uid": "deployment-uid", "controller": true }
                    ]
                  },
                  "spec": {
                    "replicas": 2,
                    "template": {
                      "metadata": { "labels": { "app": "dashboard" } },
                      "spec": {
                        "containers": [
                          { "name": "dashboard", "image": "registry.example/dashboard@sha256:abc" }
                        ]
                      }
                    }
                  },
                  "status": { "readyReplicas": 2 }
                },
                {
                  "apiVersion": "v1",
                  "kind": "Pod",
                  "metadata": {
                    "namespace": "bluetusk-web",
                    "name": "dashboard-abc-123",
                    "uid": "pod-uid",
                    "resourceVersion": "103",
                    "labels": { "app": "dashboard" },
                    "ownerReferences": [
                      { "kind": "ReplicaSet", "name": "dashboard-abc", "uid": "replicaset-uid", "controller": true }
                    ]
                  },
                  "spec": {
                    "nodeName": "worker-1",
                    "containers": [
                      { "name": "dashboard", "image": "registry.example/dashboard@sha256:abc" }
                    ]
                  },
                  "status": {
                    "phase": "Running",
                    "containerStatuses": [
                      { "name": "dashboard", "ready": true, "restartCount": 0 }
                    ]
                  }
                },
                {
                  "apiVersion": "v1",
                  "kind": "Service",
                  "metadata": {
                    "namespace": "bluetusk-web",
                    "name": "dashboard",
                    "uid": "service-uid",
                    "resourceVersion": "104"
                  },
                  "spec": {
                    "type": "LoadBalancer",
                    "clusterIP": "10.0.0.10",
                    "selector": { "app": "dashboard" },
                    "ports": [ { "name": "http", "port": 80 } ]
                  },
                  "status": { "loadBalancer": { "ingress": [ { "ip": "203.0.113.10" } ] } }
                },
                {
                  "apiVersion": "networking.k8s.io/v1",
                  "kind": "Ingress",
                  "metadata": {
                    "namespace": "bluetusk-web",
                    "name": "dashboard",
                    "uid": "ingress-uid",
                    "resourceVersion": "105"
                  },
                  "spec": {
                    "rules": [
                      {
                        "host": "dashboard.example",
                        "http": {
                          "paths": [
                            {
                              "path": "/",
                              "backend": { "service": { "name": "dashboard", "port": { "number": 80 } } }
                            }
                          ]
                        }
                      }
                    ],
                    "tls": [ { "secretName": "dashboard-tls" } ]
                  },
                  "status": { "loadBalancer": { "ingress": [ { "ip": "203.0.113.10" } ] } }
                },
                {
                  "apiVersion": "cert-manager.io/v1",
                  "kind": "Certificate",
                  "metadata": {
                    "namespace": "bluetusk-web",
                    "name": "dashboard",
                    "uid": "certificate-uid",
                    "resourceVersion": "106"
                  },
                  "spec": {
                    "secretName": "dashboard-tls",
                    "dnsNames": [ "dashboard.example" ]
                  },
                  "status": {
                    "conditions": [ { "type": "Ready", "status": "True", "reason": "Ready" } ]
                  }
                },
                {
                  "apiVersion": "discovery.k8s.io/v1",
                  "kind": "EndpointSlice",
                  "metadata": {
                    "namespace": "bluetusk-web",
                    "name": "dashboard-xyz",
                    "uid": "slice-uid",
                    "resourceVersion": "107",
                    "labels": { "kubernetes.io/service-name": "dashboard" }
                  },
                  "addressType": "IPv4",
                  "endpoints": [
                    {
                      "conditions": { "ready": true },
                      "targetRef": { "kind": "Pod", "namespace": "bluetusk-web", "name": "dashboard-abc-123" }
                    }
                  ],
                  "status": {}
                }
              ]
            }
            """);
        var observedAt = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

        var snapshot = KubernetesTopologyGraphBuilder.Build(
            resources,
            ["bluetusk-web"],
            "test/cluster",
            observedAt);

        Assert.Equal(observedAt, snapshot.ObservedAt);
        Assert.Contains(snapshot.Nodes, node =>
            node.Id == "kubernetes:deployment:bluetusk-web:dashboard" &&
            node.ResourceUid == "deployment-uid" &&
            node.ResourceVersion == "101" &&
            node.Status == "Ready");
        Assert.Contains(snapshot.Nodes, node =>
            node.Kind == "Container image" &&
            node.DisplayName == "registry.example/dashboard@sha256:abc" &&
            node.Status == "Digest pinned");
        Assert.Contains(snapshot.Nodes, node =>
            node.Kind == "External endpoint" && node.DisplayName == "203.0.113.10");
        Assert.Contains(snapshot.Edges, edge =>
            edge.Kind == "OWNS" &&
            edge.SourceId == "kubernetes:deployment:bluetusk-web:dashboard" &&
            edge.TargetId == "kubernetes:replicaset:bluetusk-web:dashboard-abc");
        Assert.Contains(snapshot.Edges, edge =>
            edge.Kind == "SELECTS" &&
            edge.SourceId == "kubernetes:service:bluetusk-web:dashboard" &&
            edge.TargetId == "kubernetes:pod:bluetusk-web:dashboard-abc-123");
        Assert.Contains(snapshot.Edges, edge =>
            edge.Kind == "ROUTES_TO" &&
            edge.TargetId == "kubernetes:service:bluetusk-web:dashboard");
        Assert.Contains(snapshot.Edges, edge => edge.Kind == "SECURES");
        Assert.Contains(snapshot.Edges, edge => edge.Kind == "TARGETS");
        Assert.DoesNotContain(snapshot.Nodes, node => node.Id == "client:browser");
        Assert.All(snapshot.Nodes, node => Assert.Equal(observedAt, node.ObservedAt));
        Assert.All(snapshot.Edges, edge => Assert.Equal(observedAt, edge.ObservedAt));
    }

    [Fact]
    public void RejectsIncompleteKubernetesIdentity()
    {
        var resources = ParseResources(
            """
            {
              "metadata": {},
              "items": [
                {
                  "apiVersion": "v1",
                  "kind": "Pod",
                  "metadata": {
                    "namespace": "bluetusk-web",
                    "name": "missing-uid",
                    "resourceVersion": "1"
                  },
                  "spec": {},
                  "status": {}
                }
              ]
            }
            """);

        _ = Assert.Throws<ArgumentException>(() =>
            KubernetesTopologyGraphBuilder.Build(
                resources,
                ["bluetusk-web"],
                "test/cluster",
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ApiSourcePaginatesEveryCollectionAndSendsTheServiceAccountToken()
    {
        var requests = new ConcurrentBag<Uri>();
        using var handler = new StubHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            requests.Add(request.RequestUri!);
            var isPodPage = request.RequestUri!.AbsolutePath.EndsWith(
                "/pods",
                StringComparison.Ordinal);
            var isContinuation = request.RequestUri.Query.Contains(
                "continue=next-page",
                StringComparison.Ordinal);
            var json = isPodPage && !isContinuation
                ? """
                  { "metadata": { "continue": "next-page" }, "items": [] }
                  """
                : isPodPage
                    ? """
                      {
                        "metadata": {},
                        "items": [
                          {
                            "apiVersion": "v1",
                            "kind": "Pod",
                            "metadata": {
                              "namespace": "bluetusk-web",
                              "name": "dashboard-1",
                              "uid": "pod-1",
                              "resourceVersion": "200"
                            },
                            "spec": { "containers": [] },
                            "status": { "phase": "Running", "containerStatuses": [] }
                          }
                        ]
                      }
                      """
                    : """
                      { "metadata": {}, "items": [] }
                      """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            };
        });
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://kubernetes.default.svc"),
        };
        var source = new KubernetesApiTopologySource(
            client,
            _ => ValueTask.FromResult("test-token"),
            ["bluetusk-web"],
            "test/cluster");

        var snapshot = await source.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(10, requests.Count);
        Assert.Contains(requests, uri => uri.Query.Contains("limit=500", StringComparison.Ordinal));
        Assert.Contains(requests, uri => uri.Query.Contains("continue=next-page", StringComparison.Ordinal));
        Assert.Contains(snapshot.Nodes, node =>
            node.Id == "kubernetes:pod:bluetusk-web:dashboard-1" &&
            node.ResourceUid == "pod-1" &&
            node.ResourceVersion == "200");
    }

    private static KubernetesResourceDocument[] ParseResources(string json) =>
        JsonSerializer.Deserialize(
            json,
            KubernetesTopologyJsonContext.Default.KubernetesResourceListDocument)!.Items;

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }
}
