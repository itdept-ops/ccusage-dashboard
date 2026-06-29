import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';

/** Shared public footer for the marketing pages. */
@Component({
  selector: 'app-marketing-footer',
  imports: [RouterLink],
  templateUrl: './marketing-footer.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './marketing-footer.scss',
})
export class MarketingFooter {
  readonly year = 2026;
  readonly repo = 'https://github.com/itdept-ops/usage-iq';

  /** Three clean columns with real hierarchy (Product / About / Built with). */
  readonly cols = [
    {
      title: 'Product',
      links: [
        { label: 'Look inside', path: '/inside' },
        { label: 'Features', path: '/features' },
        { label: 'AI', path: '/ai' },
        { label: 'How it works', path: '/how-it-works' },
      ],
    },
    {
      title: 'About',
      links: [
        { label: 'The story', path: '/about' },
        { label: "How it's built", path: '/inside' },
        { label: 'Technology', path: '/technology' },
      ],
    },
  ];

  /** "Built with" — the live stack; the last entry links out to the repo. */
  readonly stack = ['Angular', '.NET 9', 'PostgreSQL', 'Docker'];
}
