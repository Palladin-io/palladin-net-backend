# Palladin email layout

Every HTML email uses the same restrained Palladin hierarchy. New templates must include
`_header.html.liquid` with an `emailTitle`, include `_cta.html.liquid` for an action, and finish with the
localized footer. Do not duplicate or locally override the shared title, button, or footer styles.

```liquid
{% include '_header.html.liquid', emailTitle: "Email title" %}
<p style="margin:0 0 40px">One concise primary message.</p>
{% include '_cta.html.liquid', emailCtaUrl: actionUrl, emailCtaLabel: "Clear action" %}
<p style="margin:0;font-size:12px;line-height:1.55;color:#8a8a8a">Optional supporting note.</p>
{% include '_footer.en.html.liquid' %}
```

The shared shell provides the 14 px body text, 20 px title, Palladin header, 600 px card, and compact
footer. Keep primary copy left-aligned and the single CTA centered. Use 40 px before the CTA and 56 px
after it. Supporting or expiry text uses 12 px, `1.55` line height, and `#8a8a8a`. Every HTML template
must have matching English and Polish subject, HTML, and plain-text files.
