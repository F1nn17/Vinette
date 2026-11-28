{{/* The name of the chart, or the override. */}}
{{- define "vinette.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/* The name that all objects of a release share. */}}
{{- define "vinette.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{- define "vinette.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "vinette.labels" -}}
helm.sh/chart: {{ include "vinette.chart" . }}
{{ include "vinette.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{- define "vinette.selectorLabels" -}}
app.kubernetes.io/name: {{ include "vinette.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "vinette.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "vinette.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{/* The image: by digest when one is set, otherwise by tag (the appVersion if no tag is given). */}}
{{- define "vinette.image" -}}
{{- if .Values.image.digest }}
{{- printf "%s@%s" .Values.image.repository .Values.image.digest }}
{{- else }}
{{- printf "%s:%s" .Values.image.repository (default .Chart.AppVersion .Values.image.tag) }}
{{- end }}
{{- end }}

{{/* The claim of the data volume. */}}
{{- define "vinette.claimName" -}}
{{- default (include "vinette.fullname" .) .Values.persistence.existingClaim }}
{{- end }}

{{/* Where the certificate of the key store is mounted. */}}
{{- define "vinette.certDir" -}}
/run/secrets/vinette-keys
{{- end }}
