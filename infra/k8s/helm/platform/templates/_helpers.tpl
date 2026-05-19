{{- define "platform.fullname" -}}
{{- printf "%s-%s" .Release.Name .Chart.Name | trunc 63 | trimSuffix "-" }}
{{- end -}}

{{- define "platform.commonLabels" -}}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "platform.labels" -}}
app.kubernetes.io/name: {{ .Chart.Name }}
{{ include "platform.commonLabels" . }}
{{- end -}}

{{- define "platform.apiLabels" -}}
app.kubernetes.io/name: platform-api
app.kubernetes.io/component: api
{{ include "platform.commonLabels" . }}
{{- end -}}

{{- define "platform.webLabels" -}}
app.kubernetes.io/name: web
app.kubernetes.io/component: frontend
{{ include "platform.commonLabels" . }}
{{- end -}}

{{- define "platform.realtimeLabels" -}}
app.kubernetes.io/name: platform-realtime
app.kubernetes.io/component: realtime
{{ include "platform.commonLabels" . }}
{{- end -}}
