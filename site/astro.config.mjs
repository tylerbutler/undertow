// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// https://astro.build/config
export default defineConfig({
	site: 'https://undertow.tylerbutler.com',
	integrations: [
		starlight({
			title: 'Undertow Manual',
			description:
				'Reference manual for Undertow, the Fluid Framework–compatible document service in .NET.',
			social: [
				{ icon: 'github', label: 'GitHub', href: 'https://github.com/tylerbutler/undertow' },
			],
			customCss: [
				'@fontsource/archivo-narrow/700.css',
				'@fontsource-variable/archivo',
				'@fontsource/fragment-mono',
				'./src/styles/manual.css',
			],
			sidebar: [
				{ label: '1 · Introduction', slug: 'manual' },
				{ label: '2 · Operating procedure', slug: 'manual/operating-procedure' },
				{ label: '3 · Configuration', slug: 'manual/configuration' },
				{ label: '4 · Wire protocols', slug: 'manual/protocols' },
				{ label: '5 · Conformance & fixtures', slug: 'manual/conformance' },
				{ label: '6 · Divergence notes', slug: 'manual/divergences' },
				{ label: '7 · Internal organization', slug: 'manual/architecture' },
				{ label: '8 · Lineage', slug: 'manual/lineage' },
			],
		}),
	],
});
