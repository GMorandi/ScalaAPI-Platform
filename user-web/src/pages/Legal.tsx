import PublicLayout from "../components/PublicLayout";

export function Terms() {
  return <PublicLayout><article class="prose prose-slate max-w-3xl">
    <p class="eyebrow">Legal</p>
    <h1 class="title">Terms of service</h1>
    <p>ScalaAPI provides managed access to supported model providers. You are responsible for the requests, content, credentials, and destinations used with your account.</p>
    <h2>Acceptable use</h2>
    <p>Do not use the service to bypass provider restrictions, compromise accounts, or process content that you are not authorized to submit. We may suspend access when required to protect the service or comply with law.</p>
    <h2>Billing and limits</h2>
    <p>Usage is recorded against your account and may be subject to quotas, rate limits, and provider availability. Prices and quotas shown at the time of an operation are the authoritative values for that operation.</p>
    <h2>Changes</h2>
    <p>We may update these terms as the service evolves. The current version is published on this page.</p>
    <p class="text-sm text-slate-500">Last updated: 2026-08-10</p>
  </article></PublicLayout>;
}

export function Privacy() {
  return <PublicLayout><article class="prose prose-slate max-w-3xl">
    <p class="eyebrow">Legal</p>
    <h1 class="title">Privacy notice</h1>
    <p>ScalaAPI stores account, authentication, billing, usage, and operational records needed to provide and secure the service. Credentials and other secrets are encrypted at rest and are never shown in the portal.</p>
    <h2>Request data</h2>
    <p>Provider requests and responses are processed to deliver the requested operation, enforce content policy, calculate usage, and investigate reliability or security events. Redaction rules limit sensitive content in operational audit records.</p>
    <h2>Retention and access</h2>
    <p>Access to account and operational records is restricted by role and recorded in audit events. Retention follows the configured operational and legal requirements for this deployment.</p>
    <h2>Contact</h2>
    <p>Use the account support channel configured for your ScalaAPI deployment for privacy questions or data requests.</p>
    <p class="text-sm text-slate-500">Last updated: 2026-08-10</p>
  </article></PublicLayout>;
}
