import assert from 'node:assert/strict';
import { before, test } from 'node:test';

import { CoreIdml, IdmlError, Status } from '../src/index.ts';
import { fixture, fixtureText, loadEngine, png } from './helpers.ts';

// Creating, reading and editing documents through the JSON contract (include/coreidml.h).
let idml: CoreIdml;
const article = fixture('article-1photo.idml');

before(async () => {
  idml = await loadEngine();
});

test('create makes a blank A4 page, or the document a spec describes', () => {
  using blank = idml.create();
  const summary = blank.summary();
  assert.equal(summary.pageCount, 1);
  assert.equal(Math.round(summary.pageWidth), 595);
  assert.deepEqual(summary.pages[0].items, []);

  using doc = idml.create({
    unit: 'mm',
    page: { size: 'A5', margins: { all: 10 } },
    colors: [{ name: 'Brand', hex: '#0055AA' }],
    paragraphStyles: [{ name: 'Title', size: 24, color: 'Brand' }],
    pages: [{ items: [{ type: 'text', name: 'title', x: 10, y: 10, width: 128, height: 20, text: 'Hello', style: 'Title' }] }, {}],
  });
  const described = doc.summary();
  assert.equal(described.pageCount, 2);
  assert.equal(described.pages[0].items[0].name, 'title');
  assert.equal(described.pages[0].items[0].text, 'Hello');
  assert.ok(described.paragraphStyles.includes('Title'));
  assert.ok(described.swatches.includes('Brand'));
  assert.equal(doc.validate().isValid, true);
});

test('apply appends the pages of a spec; specs may be JSON text', () => {
  using doc = idml.create('{ "page": { "size": "Letter" } }');

  doc.apply({ pages: [{ items: [{ type: 'rectangle', name: 'box', x: 36, y: 36, width: 100, height: 100, fill: 'Black' }] }] });
  doc.apply('{ "pages": [ {} ] }');

  const summary = doc.summary({ includeResources: false });
  assert.equal(summary.pageCount, 3);
  assert.equal(summary.pageWidth, 612);
  assert.equal(summary.pages![1]!.items[0]!.name, 'box');
});

test('summary options choose the sections, the pages and the text length', () => {
  using doc = idml.open(article);

  const full = doc.summary();
  const links = doc.summary({ sections: ['links'] });
  const short = doc.summary({ maxTextLength: 5, includeRuns: true, includeResources: false });

  assert.equal(full.pageCount, 1);
  assert.equal(full.stories.length, 3);
  assert.deepEqual(Object.keys(links).filter((key) => Array.isArray(links[key])), ['links']);
  assert.equal(links.links[0].id, 'u21a');
  const lorem = short.stories.find((story: { id: string }) => story.id === 'u19f');
  assert.ok(lorem.text.length <= 6, lorem.text);
  assert.ok(lorem.runs.length > 0);
  assert.equal(short.paragraphStyles, undefined);
});

test('story, setRunText and replaceText edit text and keep its formatting', () => {
  using doc = idml.open(article);
  const story = doc.story('u188');
  assert.equal(story.text, 'THE HEADLINE HERE');
  assert.equal(story.runs[0].index, 0);

  doc.setRunText('u188', 0, 'A new headline');
  const replaced = doc.replaceText('Lorem', 'Ipsum');

  assert.equal(doc.story('u188').text, 'A new headline');
  assert.equal(doc.story('u188').runs[0].paragraphStyle, story.runs[0].paragraphStyle);
  assert.equal(replaced, 2);
  assert.match(doc.story('u19f').text, /^Ipsum ipsum/);
  assert.equal(doc.replaceText('not in the document', 'x'), 0);
  assert.throws(() => doc.setRunText('u188', 99, 'x'), { name: 'IdmlError', code: Status.notFound });
  assert.throws(() => doc.story('nope'), { name: 'IdmlError', code: Status.notFound });
  assert.throws(() => doc.replaceText('', 'x'), { name: 'IdmlError', code: Status.invalidArgument });
  assert.throws(() => doc.setRunText('u188', 0.5, 'x'), { name: 'IdmlError', code: Status.invalidArgument });
});

test('the XML structure is read as a tree and exported as XML', () => {
  using doc = idml.open(article);

  const tree = doc.xmlStructure();
  const all = doc.exportXml();
  const headline = doc.exportXml({ at: '/Root/module[1]/headline[1]' });

  assert.equal(tree?.tag, 'Root');
  assert.deepEqual(
    tree?.children[0].children.map((child: { tag: string }) => child.tag),
    ['main_picture', 'headline', 'Story'],
  );
  assert.match(all, /^<Root><module>/);
  assert.equal(headline, '<headline>THE HEADLINE HERE</headline>');
  assert.throws(() => doc.exportXml({ at: '/Root/nope[1]' }), { name: 'IdmlError', code: Status.notFound });

  // A new document has an empty root element.
  using blank = idml.create();
  assert.deepEqual(blank.xmlStructure(), { tag: 'Root', id: blank.xmlStructure()?.id, path: '/Root' });
  assert.equal(blank.exportXml(), '<Root />');
});

test('importXml writes text, links and mapped inline tags onto the structure', () => {
  using doc = idml.open(fixture('article-1photo_import-xml.idml'));

  const report = doc.importXml(fixtureText('xml/article-1photo_import-xml.xml'), { at: '/Root/module[1]' });

  assert.deepEqual(report.unmatched, []);
  assert.equal(report.added, 3);
  assert.equal(doc.exportXml({ at: '/Root/module[1]/headline[1]' }), '<headline>The Life Aquatic with Steve Zissou</headline>');
  assert.equal(doc.validate().isValid, true);
  assert.throws(() => doc.importXml('<not xml'), { name: 'IdmlError', code: Status.invalidFormat });
});

test('validate reports a valid document', () => {
  using doc = idml.open(article);

  const report = doc.validate();

  assert.equal(report.isValid, true);
  assert.deepEqual(report.issues, []);
});

test('diff lists what changed between two documents', () => {
  using before = idml.open(article);
  using after = idml.open(article);
  after.setRunText('u188', 0, 'Changed');

  const same = before.diff(before);
  const changed = before.diff(after);

  assert.deepEqual(same, { identical: true, changes: [] });
  assert.equal(changed.identical, false);
  assert.deepEqual(changed.changes, [{ kind: 'run-changed', id: 'u188', runIndex: 0, before: 'THE HEADLINE HERE', after: 'Changed' }]);
});

test('importPages appends the pages of another document, or of itself', () => {
  using doc = idml.create();
  using source = idml.open(article);

  assert.equal(doc.importPages(source), 1);
  assert.equal(doc.importPages(doc, [0, 1]), 2);

  const summary = doc.summary({ includeResources: false, maxTextLength: -1 });
  assert.equal(summary.pageCount, 4);
  assert.ok(summary.stories.some((story: { text: string }) => story.text === 'THE HEADLINE HERE'));
  assert.equal(doc.validate().isValid, true);
  assert.throws(() => doc.importPages(source, [7]), { name: 'IdmlError', code: Status.notFound });
  assert.throws(() => doc.importPages(source, ['0' as unknown as number]), { name: 'IdmlError', code: Status.invalidArgument });
});

test('fill puts text and images (files of the in-memory file system) into a template', () => {
  using doc = idml.open(article);
  idml.writeFile('/content/photo.png', png(64, 48));

  const report = doc.fill({ text: { headline: 'Filled headline', nothing: 'x' }, images: { main_picture: '/content/photo.png' }, fit: 'fit' });

  assert.deepEqual(report.filled, { headline: 1, nothing: 0, main_picture: 1 });
  assert.deepEqual(report.missing, ['nothing']);
  assert.equal(doc.story('u188').text, 'Filled headline');
  assert.equal(doc.summary({ sections: ['links'] }).links[0].uri, 'file:/content/photo.png');
});

test('relink points a link at a file of the in-memory file system and fits the image again', () => {
  using doc = idml.open(article);
  idml.writeFile('/content/wide.png', png(200, 50));

  doc.relink('u21a', '/content/wide.png', { refit: true });

  const [link] = doc.summary({ sections: ['links'] }).links;
  assert.equal(link.uri, 'file:/content/wide.png');
  assert.equal(link.format, '$ID/Portable Network Graphics (PNG)');
  assert.throws(() => doc.relink('nope', '/content/wide.png'), { name: 'IdmlError', code: Status.notFound });
  assert.throws(() => doc.relink('u21a', ''), { name: 'IdmlError', code: Status.invalidArgument });
});

test('a document spec the library cannot use is an IdmlError', () => {
  assert.throws(() => idml.create({ pages: 'one' }), { name: 'IdmlError', code: Status.invalidArgument });
  assert.throws(() => idml.create({ page: { size: 'Nope' } }), (error: unknown) => error instanceof IdmlError && /Unknown page size/.test(error.message));
  using doc = idml.create();
  assert.throws(() => doc.diff('other' as never), { name: 'IdmlError', code: Status.invalidArgument });
  const closed = idml.create();
  closed.close();
  assert.throws(() => doc.importPages(closed), { name: 'IdmlError', code: Status.invalidHandle });
  assert.throws(() => closed.summary(), { name: 'IdmlError', code: Status.invalidHandle });
});
