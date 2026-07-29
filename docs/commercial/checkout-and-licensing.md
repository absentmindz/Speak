# Checkout and licensing foundation

## Current state

The desktop app now has provider-neutral commercial links under `Commercial` in `appsettings.json`:

```json
{
  "Commercial": {
    "WebsiteUrl": "https://absentmindz.github.io/Speak/",
    "PricingUrl": "https://absentmindz.github.io/Speak/#pricing",
    "CheckoutUrl": "https://absentmindz.github.io/Speak/#founding",
    "SupportUrl": "https://github.com/absentmindz/Speak/issues",
    "LicensePortalUrl": ""
  }
}
```

Only public HTTPS links without embedded credentials are accepted. The application never stores a Lemon Squeezy API key, webhook secret, or signing secret.

The default checkout route is an interest-registration page, not a payment claim. `LicensePortalUrl` remains empty until a real entitlement service exists.

## Recommended production architecture

1. Create the product and variant in Lemon Squeezy (or another merchant of record).
2. Point `CheckoutUrl` at the public hosted checkout URL.
3. Receive order/license webhooks on a small server-side endpoint.
4. Verify every webhook signature server-side.
5. Store only the minimum entitlement record required for support and activation.
6. Issue a signed entitlement token that the desktop app can verify with an embedded public key.
7. Keep the Community app usable when offline or when the licensing service is unavailable.
8. Use `LicensePortalUrl` for customer-managed receipts, devices, or license recovery.

## Entitlement rules

- Community features in this repository remain available under Apache-2.0.
- A Pro entitlement may unlock separately developed convenience features, official support, managed installers/model packs, or team administration.
- Revocation must not delete user data or disable Community functionality.
- Failed network checks must degrade to the last valid signed entitlement during a documented grace period.
- Never log full license keys, checkout tokens, webhook bodies, email addresses, or payment details.

## Required decisions before live checkout

- Merchant account owner and legal seller name.
- Product/variant IDs and final price/currency.
- Refund, support, and tax policy.
- Privacy policy URL and terms for paid support.
- Webhook host, signing-key custody, and data-retention period.
- Exact Pro benefits that are not already Apache-licensed Community features.
