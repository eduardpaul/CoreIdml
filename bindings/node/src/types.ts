// Shapes of the JSON the library takes and returns (coreidml.h). Options are sent as they are written here
// (camelCase, the names the library reads). Results name the fields callers rely on and keep an index signature, so
// fields a later library adds stay reachable.

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
  /** Image file of an image item. */
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
  cellInset?: number;
  [field: string]: unknown;
}

export interface PageSpec {
  index?: number;
  margins?: Spacing;
  items?: ItemSpec[];
}

/** A document to create, or to add to one ({@link IdmlDocument.apply}): page setup, colours, styles, pages. */
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

/** Data for {@link IdmlDocument.fill}. Keys match frame names, script labels, XML tags, then `{{key}}` placeholders. */
export interface FillData {
  text?: Record<string, string>;
  /** Image keys to local JPEG or PNG files. */
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
  /** Adds every story's indexed content runs (the indices {@link IdmlDocument.setRunText} takes). */
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
  /** Holds special characters (a page number): {@link IdmlDocument.setRunText} needs `force`. */
  special?: boolean;
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

/** What {@link IdmlDocument.summary} returns. Sections left out by the options are missing. */
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
  /** Aborting cancels the call after the current page. */
  signal?: AbortSignal;
}

export interface PdfExportOptions extends ProgressOptions {
  /** 1-based page numbers; every page by default. */
  pages?: number[];
  /** Folders with .ttf, .otf or .ttc files, searched before the system's fonts. */
  fontDirectories?: string[];
  /** Also use the fonts installed on the machine. Default true. */
  useSystemFonts?: boolean;
  /** Font family replacements, e.g. `{ 'Minion Pro': 'Liberation Serif' }`. */
  substitutions?: Record<string, string>;
  /** Families tried, in order, for a font that is not found. */
  fallbackFamilies?: string[];
  /** Folders searched for placed images by file name. */
  linkDirectories?: string[];
  /** A link's file name (or URI) to a local file. */
  linkRemap?: Record<string, string>;
  /** Where the .idml lives, so relative links resolve (for a document opened from bytes). */
  documentPath?: string;
  /** Downsamples placed images above this resolution at their placed size. Off by default. */
  maxImageDpi?: number;
  /** Quality (1-100) of JPEGs re-encoded by `maxImageDpi`. Default 85. */
  jpegQuality?: number;
  /** Draws a box where an image is missing. */
  markMissingImages?: boolean;
  /** Draws the items of master spreads. Default true. */
  includeMasterItems?: boolean;
  /** Title in the PDF's metadata. */
  title?: string;
  [option: string]: unknown;
}

export interface PdfImportOptions extends ProgressOptions {
  /** 1-based page numbers; every page by default. */
  pages?: number[];
  /** Where extracted images are written: IDML links images, it cannot embed them. */
  imageDirectory?: string;
  preserveLineBreaks?: boolean;
  /** Rebuilds vector shapes. Default true. */
  importShapes?: boolean;
  /** Font folders the fonts the PDF uses are checked against (see the report's `fonts`). */
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
