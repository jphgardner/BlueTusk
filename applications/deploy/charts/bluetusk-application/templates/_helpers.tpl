{{- define "bluetusk.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- define "bluetusk.fullname" -}}
{{- printf "%s-%s" .Release.Name (include "bluetusk.name" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- define "bluetusk.labels" -}}
app.kubernetes.io/name: {{ include "bluetusk.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
bluetusk.io/environment: {{ .Values.environment | quote }}
{{- end -}}
{{- define "bluetusk.image" -}}
{{- if or (not .repository) (not (regexMatch "^sha256:[a-f0-9]{64}$" .digest)) -}}
{{- fail "all application images must be GHCR repositories pinned by sha256 digest" -}}
{{- end -}}
{{- printf "%s@%s" .repository .digest -}}
{{- end -}}
{{- define "bluetusk.podSecurity" -}}
allowPrivilegeEscalation: false
capabilities: { drop: ["ALL"] }
readOnlyRootFilesystem: true
runAsNonRoot: true
seccompProfile: { type: RuntimeDefault }
{{- end -}}
