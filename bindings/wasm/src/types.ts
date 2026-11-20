// Shapes of the JSON the library takes and returns (include/coreidml.h), the same as in @coreidml/node. Options are
// sent as they are written here (camelCase, the names the library reads). Results name the fields callers rely on
// and keep an index signature, so fields a later library adds stay reachable. In WebAssembly every path is a path
// in the engine's in-memory file system: fonts, placed images and the images of a PDF import are files there (see
// CoreIdml.addFonts, addLinks and writeFile).

/** Any JSON object the library returns. */
export type JsonObject = { [key: string]: any };

// ---- Document specs ----------------------------------------------------------------------------------------------

/** Margins, insets: one value for every side, or each side. */
export interface Spacing {
  all?: number;
  top?: number;
  right?: number;
  bottom?: number;
  left?: number;
}

/** Character and paragraph formatting shared by styles, paragraphs and runs. */
export interface TextFormat {
  font?: string;
  fontStyle?: string;
  size?: number;
  leading?: number;
  /** A colour name from `colors`, or a built-in swatch such as `Black`. */
  color?: string;
  tint?: number;
  /** `left`, `center`, `right`, `justify`... */
  align?: string;
  spaceBefore?: number;
  spaceAfter?: number;
  firstLineIndent?: number;
  leftIndent?: number;
  rightIndent?: number;
  underline?: boolean;
  strikeThrough?: boolean;
  tracking?: number;
  caps?: string;
  position?: string;
  /** Paragraphs and paragraph styles: `bullet`, `numbered` or `none`. InDesign draws the bullet or number; leave it out of the text. */
  list?: string;
  /** The bullet character; default `•`. */
  bullet?: string;
  /** Text after the bullet; default a tab. `^m` em space, `^>` en space. */
  bulletAfter?: string;
  /** `1` (default), `01`, `001`, `0001`, `a`, `A`, `i`, `I`. */
  numberFormat?: string;
  /** `^#` the number, `^1`-`^9` that level's number, a tab or `^t` a tab. Default `^#.^t`. */
  numberText?: string;
  /** 1-9; a level restarts after any paragraph of a higher level. */
  listLevel?: number;
  /** On a paragraph, restart the numbering at this number; on a style, the first number. */
  startAt?: number;
  /** Paragraphs and paragraph styles: tab stops, replacing the style's; `[]` clears them. Put a tab character in the text. */
  tabs?: TabSpec[];
  /** Paragraphs and paragraph styles: keep the last line with the next paragraph's first N lines (0 to 5; headings: 1). */
  keepWithNext?: number;
  /** Never break the paragraph across columns or frames. */
  keepTogether?: boolean;
  /** Lines kept before a column break (orphans; InDesign's default 2). */
  keepFirstLines?: number;
  /** Lines kept after a column break (widows; InDesign's default 2). */
  keepLastLines?: number;
  /** `anywhere` (default), `nextColumn`, `nextFrame`, `nextPage`, `nextOddPage` or `nextEvenPage`. */
  startParagraph?: string;
  /** Paragraphs and paragraph styles: drop cap this many lines deep (0 removes it). */
  dropCapLines?: number;
  /** Characters in the drop cap; default 1. */
  dropCapCharacters?: number;
  /** Character style of the drop cap. */
  dropCapStyle?: string;
  /** Character styles in sequence from the paragraph's start (a bold lead-in); `[]` clears them. */
  nestedStyles?: NestedStyleSpec[];
  /** Character styles on the text GREP expressions match; `[]` clears them. */
  grepStyles?: GrepStyleSpec[];
  /** Put the baselines on the document's baseline grid (`page.baselineGrid`). */
  alignToBaselineGrid?: boolean;
  /** With `alignToBaselineGrid`: only the first line snaps to the grid. */
  gridFirstLineOnly?: boolean;
  /** Span the frame's columns: 0 all, N the first N, 1 back to a single column. */
  spanColumns?: number;
  /** Split the paragraph's column into N sub-columns (1: none). */
  splitColumns?: number;
  /** Points between split sub-columns; default 6. */
  splitGutter?: number;
}

/** A nested style: a character style through (or up to) the `count`th delimiter. Give `through` or `upTo`. */
export interface NestedStyleSpec {
  /** Character style. */
  style: string;
  /** `words`, `characters`, `sentences`, `letters`, `digits`, `tabs`, `lineBreak`, `endNestedStyle`, or one character. */
  through?: string;
  /** Same values; the style ends before the delimiter. */
  upTo?: string;
  /** Default 1. */
  count?: number;
}

/** A GREP style: a character style on every match of an InDesign GREP expression. */
export interface GrepStyleSpec {
  /** Character style. */
  style: string;
  expression: string;
}

/** A tab stop. */
export interface TabSpec {
  /** Points from the frame's left edge, inside its inset. */
  position: number;
  /** `left` (default), `center`, `right` or `decimal` (`alignOn` sits on the stop). */
  align?: string;
  /** Repeated across the tab's space: `.`, `-`, `_`, `·` (up to 8 characters). */
  leader?: string;
  /** For `decimal`: the character to align on, default `.`. */
  alignOn?: string;
}

export interface StyleSpec extends TextFormat {
  name: string;
  basedOn?: string;
}

export interface ColorSpec {
  name: string;
  /** Four values, 0-100. */
  cmyk?: number[];
  /** Three values, 0-255. */
  rgb?: number[];
  /** `#RRGGBB`. */
  hex?: string;
}

export interface PageSetupSpec {
  /** A named size such as `A4`, `A5` or `Letter`. */
  size?: string;
  width?: number;
  height?: number;
  landscape?: boolean;
  margins?: Spacing;
  facingPages?: boolean;
  columns?: number;
  columnGutter?: number;
  bleed?: number;
  /** The document baseline grid, for paragraphs with `alignToBaselineGrid`. */
  baselineGrid?: BaselineGridSpec;
}

/** Grid lines every `increment` points (the body leading) from `start` (in the spec's unit; default 36 pt) below the top of the page or the top margin. */
export interface BaselineGridSpec {
  start?: number;
  increment: number;
  /** `page` (default) or `topMargin`. */
  relativeTo?: string;
}

export interface RunSpec extends TextFormat {
  text: string;
  /** Character style. */
  style?: string;
}

export interface ParagraphSpec extends TextFormat {
  text?: string;
  /** Paragraph style. */
  style?: string;
  runs?: RunSpec[];
}

/** One item on a page: a text frame, an image, a table or a shape (`type`). Positions are in the spec's unit. */
export interface ItemSpec {
  /** `text` (the default), `image`, `table`, `rectangle`, `ellipse`, `line`... */
  type?: string;
  /** Frame name: a key for {@link FillData}. */
  name?: string;
  /** Script label: a key for {@link FillData}. */
  label?: string;
  /** XML tag the item is bound to. */
  tag?: string;
  layer?: string;
  x: number;
  y: number;
  width: number;
  height: number;
  x2?: number;
  y2?: number;
  fill?: string;
  fillTint?: number;
  /** Rectangles and ellipses have no outline unless `stroke` is given; lines default to Black, 1 pt. */
  stroke?: string;
  strokeWeight?: number;
  strokeTint?: number;
  strokeType?: 'solid' | 'dashed' | 'dotted';
  /** Ends of lines. */
  endCap?: 'butt' | 'round' | 'projecting';
  /** Opacity of the whole item, 0 to 100. */
  opacity?: number;
  cornerRadius?: number;
  rotation?: number;
  /** Text of a text frame; may hold `{{placeholders}}`. */
  text?: string;
  /** Paragraph style of `text`. */
  style?: string;
  paragraphs?: ParagraphSpec[];
  columns?: number;
  columnGutter?: number;
  inset?: Spacing;
  verticalAlign?: string;
  /** Name of the frame this one continues (threaded text). */
  continueFrom?: string;
  /** Image of an image item: a JPEG or PNG in the in-memory file system, e.g. `/links/photo.jpg`. */
  path?: string;
  /** Image fit: `fill`, `fit`, `stretch`, `center` or `original`. */
  fit?: string;
  /** Table cells, row by row. */
  rows?: string[][];
  columnWidths?: number[];
  headerRows?: number;
  rowHeight?: number;
  cellStyle?: string;
  headerStyle?: string;
  headerFill?: string;
  /** Swatch of every other body row, from the first (zebra stripes). */
  alternateFill?: string;
  cellInset?: number;
  [field: string]: unknown;
}

export interface PageSpec {
  index?: number;
  margins?: Spacing;
  items?: ItemSpec[];
}

/** A document to create, or to add to one (`apply`): page setup, colours, styles, pages. */
export interface DocumentSpec {
  /** Unit of every length: `pt` (the default), `mm`, `cm`, `in`. */
  unit?: string;
  page?: PageSetupSpec;
  colors?: ColorSpec[];
  paragraphStyles?: StyleSpec[];
  characterStyles?: StyleSpec[];
  pages?: PageSpec[];
  [field: string]: unknown;
}

// ---- Filling and editing -----------------------------------------------------------------------------------------

/** Data for `fill`. Keys match frame names, script labels, XML tags, then `{{key}}` placeholders. */
export interface FillData {
  text?: Record<string, string>;
  /** Image keys to JPEG or PNG files of the in-memory file system, e.g. `/links/photo.jpg`. */
  images?: Record<string, string>;
  /** Image fit: `fill` (the default), `fit`, `stretch`, `center` or `original`. */
  fit?: string;
  [field: string]: unknown;
}

export interface FillReport extends JsonObject {
  /** Places filled, per key. */
  filled: Record<string, number>;
  /** Keys that matched nothing. */
  missing: string[];
}

export interface ImportXmlOptions {
  /** Structure element to import onto, e.g. `/Root/article[2]`; the root by default. */
  at?: string;
  /** Fit of images placed from `href`. */
  fit?: string;
  /** Nested inline tags to character styles, e.g. `{ b: 'Bold' }`. */
  characterStyles?: Record<string, string>;
  /** Only relink images from `href`; never measure or fit them again. */
  relinkOnly?: boolean;
  [option: string]: unknown;
}

export interface ImportXmlReport extends JsonObject {
  updated: number;
  added: number;
  deleted: number;
  unmatched: string[];
  warnings: string[];
}

// ---- Reading -----------------------------------------------------------------------------------------------------

export interface SummaryOptions {
  /** Characters of text per frame and story: 0 for none, -1 for all. Default 200. */
  maxTextLength?: number;
  /** Adds every story's indexed content runs (the indices `setRunText` takes). */
  includeRuns?: boolean;
  /** Styles, swatches, layers, tags and fonts. Default true. */
  includeResources?: boolean;
  /** Only these 0-based pages, with their stories and links. */
  pages?: number[];
  /** Only these parts: `pages`, `offPage`, `stories`, `links`, `resources`. */
  sections?: Array<'pages' | 'offPage' | 'stories' | 'links' | 'resources'>;
}

export interface ItemSummary extends JsonObject {
  id: string;
  kind: string;
  name?: string;
  label?: string;
  tag?: string;
  /** Page-relative position in points. */
  x: number;
  y: number;
  width: number;
  height: number;
  storyId?: string;
  text?: string;
  /** The story probably does not fit its frames (an estimate). */
  likelyOverset?: boolean;
  linkId?: string;
  imageUri?: string;
  /** The object style applied to the item, when it is not a built-in one (`[None]`, `[Normal Text Frame]`, ...). */
  objectStyle?: string;
}

export interface PageSummary extends JsonObject {
  index: number;
  id: string;
  spreadId: string;
  name: string;
  width: number;
  height: number;
  items: ItemSummary[];
}

export interface RunSummary extends JsonObject {
  index: number;
  text: string;
  paragraphStyle?: string;
  characterStyle?: string;
  /** `body`, `cell column:row`, `footnote` or `note`. */
  scope: string;
  /** Holds special characters (a page number): `setRunText` needs `force`. */
  special?: boolean;
  /** Recorded with Track Changes. `deleted` text is not shown (nor in the story text); it keeps its index. */
  change?: "inserted" | "deleted" | "moved";
}

export interface StorySummary extends JsonObject {
  id: string;
  text?: string;
  wordCount: number;
  runCount: number;
  tables: number;
  /** Frames showing the story, in thread order. */
  frames: string[];
  runs?: RunSummary[];
}

export interface LinkSummary extends JsonObject {
  id: string;
  uri: string;
  format?: string;
  frameId?: string;
  pageIndex?: number;
  /** `spread`, `master` or `story` (anchored in text). */
  location: string;
  actualPpi?: string;
  effectivePpi?: string;
}

/** What `summary` returns. Sections left out by the options are missing. */
export interface DocumentSummary extends JsonObject {
  domVersion: string;
  pageWidth: number;
  pageHeight: number;
  facingPages: boolean;
  pageCount: number;
  pages?: PageSummary[];
  offPage?: ItemSummary[];
  stories?: StorySummary[];
  links?: LinkSummary[];
  paragraphStyles?: string[];
  characterStyles?: string[];
  objectStyles?: string[];
  swatches?: string[];
  layers?: string[];
  tags?: string[];
  fonts?: string[];
}

export interface XmlNode extends JsonObject {
  tag: string;
  id: string;
  path: string;
  /** `story`, `frame` or `graphic`. */
  boundTo?: string;
  contentId?: string;
  attributes?: Record<string, string>;
  text?: string;
  children?: XmlNode[];
}

export interface ValidationIssue extends JsonObject {
  severity: string;
  code: string;
  message: string;
  part?: string;
}

export interface ValidationReport extends JsonObject {
  isValid: boolean;
  issues: ValidationIssue[];
}

export interface DiffChange extends JsonObject {
  /** `story-added`, `story-removed`, `run-changed`, `runs-restructured`, `link-changed`, `link-added`, `link-removed`, `page-count`, `item-added`, `item-removed`, `item-geometry` (page-relative bounds), `item-changed` (changed settings as name=value). */
  kind: string;
  id?: string;
  runIndex?: number;
  before?: string;
  after?: string;
}

export interface DiffReport extends JsonObject {
  identical: boolean;
  changes: DiffChange[];
}

// ---- PDF ---------------------------------------------------------------------------------------------------------

/** Called after each page with the pages done and the total. Returning `false` cancels; so does throwing. */
export type ProgressCallback = (done: number, total: number) => boolean | void;

/** How a PDF call is followed and stopped. These two are not sent to the library. */
export interface ProgressOptions {
  onProgress?: ProgressCallback;
  /**
   * Aborting cancels the call after the current page. It matters for `IdmlWorker`, where the page stays free while
   * the worker renders (see its notes on cross-origin isolation); a synchronous call checks it between pages.
   */
  signal?: AbortSignal;
}

export interface PdfExportOptions extends ProgressOptions {
  /** 1-based page numbers; every page by default. */
  pages?: number[];
  /** Folders of the in-memory file system with .ttf, .otf or .ttc files. Default: `['/fonts']` once it has files. */
  fontDirectories?: string[];
  /** The engine finds no system fonts in WebAssembly; `false` says so explicitly. */
  useSystemFonts?: boolean;
  /** Font family replacements, e.g. `{ 'Minion Pro': 'Liberation Serif' }`. */
  substitutions?: Record<string, string>;
  /** Families tried, in order, for a font that is not found. */
  fallbackFamilies?: string[];
  /** Folders searched for placed images by file name. Default: `['/links']` once it has files. */
  linkDirectories?: string[];
  /** A link's file name (or URI) to a file of the in-memory file system. */
  linkRemap?: Record<string, string>;
  /** Where the .idml would live in the in-memory file system, so relative links resolve. */
  documentPath?: string;
  /** Downsamples placed images above this resolution at their placed size. Off by default. */
  maxImageDpi?: number;
  /** Quality (1-100) of JPEGs re-encoded by `maxImageDpi`. Default 85. */
  jpegQuality?: number;
  /** Draws a box where an image is missing. */
  markMissingImages?: boolean;
  /** Draws the items of master spreads. Default true. */
  includeMasterItems?: boolean;
  /** Pages with the document's bleed around the trim size (TrimBox inside MediaBox and BleedBox). Default false: trim-size pages. */
  includeBleed?: boolean;
  /** Title in the PDF's metadata. */
  title?: string;
  [option: string]: unknown;
}

export interface PdfImportOptions extends ProgressOptions {
  /** 1-based page numbers; every page by default. */
  pages?: number[];
  /** Where extracted images are written in the in-memory file system. Default: a new `/import/<n>` folder, removed when the document is closed. */
  imageDirectory?: string;
  preserveLineBreaks?: boolean;
  /** Rebuilds vector shapes. Default true. */
  importShapes?: boolean;
  /** Font folders the fonts the PDF uses are checked against. Default: `['/fonts']` once it has files. */
  fontDirectories?: string[];
  useSystemFonts?: boolean;
  [option: string]: unknown;
}

/** A font that was not found, and what was drawn instead. */
export interface FontSubstitution extends JsonObject {
  requestedFamily: string;
  requestedStyle: string;
  usedFont: string;
  reason: string;
  embeddingRestricted: boolean;
  stories: string[];
  pages: string[];
}

export interface PdfExportReport extends JsonObject {
  pageCount: number;
  missingFonts: FontSubstitution[];
  missingImages: string[];
  oversetStories: string[];
  warnings: string[];
}

export interface ImportedFont extends JsonObject {
  family: string;
  style: string;
  pdfFontName: string;
  pages: number[];
  /** Whether the font is in the folders given (missing when no folders were given). */
  available?: boolean;
}

export interface PdfImportReport extends JsonObject {
  textFrames: number;
  imageFrames: number;
  shapes: number;
  /** Files written to `imageDirectory`. */
  images: string[];
  fonts: ImportedFont[];
  warnings: string[];
}

/** What `exportPdfBytes` returns. */
export interface PdfExport {
  pdf: Uint8Array;
  report: PdfExportReport;
}
