<script setup lang="ts">
import { computed } from "vue";
import type { CertificateReport } from "../api";
import Card from "./Card.vue";
import Note from "./Note.vue";

/**
 * When the front door stops working, and who has to do something about it.
 *
 * The own-certificate path ends with an instance that stops answering in ninety days with nobody warned.
 * This is the warning. The two thresholds behind `expiring` are the module's rather than this page's:
 * Traefik renews with a third of the lifetime left, so warning at thirty days would fire on every
 * healthy Let's Encrypt instance for the moment before a renewal that was always going to happen.
 */

const props = defineProps<{ reports: readonly CertificateReport[] }>();

/**
 * The design system's badge modifiers are ticket statuses, and only some of them mean what is needed
 * here.
 *
 * `--pending` is amber and `--resolved` is green, which is exactly right for a certificate that is
 * close to expiry and one that is fine. But there is no failure modifier: `--new` is the cyan of a
 * freshly opened ticket, and a certificate that has expired or covers the wrong name read as
 * information rather than as something broken.
 *
 * So the two failures are toned by hand, which the design system explicitly sanctions for values the
 * scale does not carry — the theme's own custom properties are readable directly. See its README on
 * reaching for `var(--color-…)` rather than an arbitrary class that does not exist.
 */
const BADGES: Record<string, string> = {
    expiring: "s-badge--pending",
    valid: "s-badge--resolved",
};

const FAILED = {
    background: "color-mix(in srgb, var(--color-danger) 14%, transparent)",
    color: "var(--color-danger)",
    borderColor: "color-mix(in srgb, var(--color-danger) 35%, transparent)",
} as const;

interface Row {
    readonly host: string;
    readonly purpose: string;
    readonly badge: string;
    readonly verdict: string;
    readonly detail: string;

    /** Expired, not yet valid, or the wrong name: one tone, because one consequence. */
    readonly failed?: boolean;

    /** Why the certificate does not cover the name it is served on, when it does not. */
    readonly misnamed: string | undefined;
}

/**
 * The module's report, turned into the one line a person reads.
 *
 * Done here rather than in the template because the report is a union — a certificate that could not be
 * read has no dates to have an opinion about — and a template that reaches for `days` on the wrong arm
 * of it renders "undefined days left" rather than failing.
 */
const rows = computed<readonly Row[]>(() =>
    props.reports.map((report) => {
        // `not-applicable` and `unreadable` are the module saying it has nothing to report rather than
        // reporting nothing: there is no certificate here to have dates or a name, only a sentence
        // saying why. That sentence is the whole row.
        //
        // Told apart by the missing `coverage` rather than by the two verdicts that mean it, because
        // the verdict is a set of names on both arms of the union and TypeScript will not subtract two
        // of them from a set to prove which arm this is. Asking whether there is a certificate to
        // describe rules the arm out where naming the verdicts does not.
        if (!("coverage" in report))
            return {
                host: report.host,
                purpose: report.purpose,
                badge: "s-badge--closed",
                verdict: report.verdict,
                detail: report.why,
                misnamed: undefined,
            };

        // A certificate whose dates are fine but which does not cover the name it is served on is not
        // "valid" in any sense the operator cares about — the handshake fails and the visitor sees a
        // browser warning. The module keeps the two facts apart, which is right; showing a green badge
        // above a red sentence is not.
        const coverage = report.coverage;
        const misnamed = coverage.covers === false;

        const days = report.days;
        const plural = Math.abs(days) === 1 ? "" : "s";
        const issuer = report.certificate?.issuer?.commonName ?? "an unnamed issuer";

        return {
            host: report.host,
            purpose: report.purpose,
            badge: BADGES[report.verdict] ?? "s-badge--closed",

            // Expired, not yet valid, or serving a name it does not cover: three different facts, one
            // consequence — a visitor gets a browser warning and nobody attributes it to this.
            failed: misnamed || report.verdict === "expired" || report.verdict === "not-yet-valid",
            verdict: misnamed ? "wrong name" : report.verdict,
            detail:
                `${days < 0 ? `expired ${-days}` : `${days}`} day${plural}${days < 0 ? " ago" : " left"}` +
                ` · renewed by ${report.renewal} · issued by ${issuer}`,
            misnamed: coverage.covers === false ? coverage.why : undefined,
        };
    }),
);
</script>

<template>
  <!-- No certificates to report is not an empty section; it is an instance where TLS is somebody else's. -->
  <Card v-if="rows.length > 0" title="Certificates">
    <div class="flex flex-col gap-3">
      <div v-for="(row, index) in rows" :key="index" class="flex flex-col gap-1">
        <div class="flex items-center gap-2 flex-wrap">
          <span class="mono text-sm text-text-primary">{{ row.host }}</span>
          <span class="text-xs text-text-muted">{{ row.purpose }}</span>
          <span class="s-badge" :class="row.failed ? undefined : row.badge" :style="row.failed ? FAILED : undefined">
            {{ row.verdict }}
          </span>
        </div>

        <p class="text-xs text-text-muted leading-relaxed">{{ row.detail }}</p>

        <!--
          A certificate that does not cover the name it is served on is a real misconfiguration — an
          origin certificate for the wrong host — and it surfaces to a visitor as a browser error nobody
          attributes to it. This page is the only place it is visible before that happens.
        -->
        <Note v-if="row.misnamed" tone="danger">{{ row.misnamed }}</Note>
      </div>
    </div>
  </Card>
</template>
