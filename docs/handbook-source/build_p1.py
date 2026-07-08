from reportlab.lib.pagesizes import A4
from reportlab.lib import colors
from reportlab.lib.units import cm, mm
from reportlab.pdfgen import canvas
from reportlab.platypus import (
    BaseDocTemplate, PageTemplate, Frame, Paragraph, Spacer, PageBreak,
    Table, TableStyle, KeepTogether, ListFlowable, ListItem, Flowable
)
from reportlab.platypus.doctemplate import NextPageTemplate
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.enums import TA_LEFT, TA_CENTER, TA_JUSTIFY, TA_RIGHT

# Design tokens
NAVY        = colors.HexColor('#1e2a5e')
NAVY_DARK   = colors.HexColor('#141d47')
INDIGO      = colors.HexColor('#4f46e5')
INDIGO_SOFT = colors.HexColor('#eef2ff')
EMERALD     = colors.HexColor('#059669')
EMERALD_SFT = colors.HexColor('#ecfdf5')
AMBER       = colors.HexColor('#d97706')
AMBER_SFT   = colors.HexColor('#fffbeb')
ROSE        = colors.HexColor('#e11d48')
ROSE_SFT    = colors.HexColor('#fff1f2')
SLATE_50    = colors.HexColor('#f8fafc')
SLATE_100   = colors.HexColor('#f1f5f9')
SLATE_200   = colors.HexColor('#e2e8f0')
SLATE_300   = colors.HexColor('#cbd5e1')
SLATE_500   = colors.HexColor('#64748b')
SLATE_700   = colors.HexColor('#334155')
SLATE_900   = colors.HexColor('#0f172a')
WHITE       = colors.white

BASE_FONT   = 'Helvetica'
BOLD_FONT   = 'Helvetica-Bold'
ITALIC_FONT = 'Helvetica-Oblique'
MONO_FONT   = 'Courier'

PAGE_W, PAGE_H = A4
MARGIN_X   = 2.0 * cm
MARGIN_TOP = 2.6 * cm
MARGIN_BOT = 2.0 * cm
CONTENT_W  = PAGE_W - 2*MARGIN_X

current_chapter = {'num': 0, 'title': ''}

def draw_cover_bg(canv, doc):
    canv.setFillColor(NAVY)
    canv.rect(0, 0, PAGE_W, PAGE_H, fill=1, stroke=0)
    canv.setFillColor(INDIGO)
    p = canv.beginPath()
    p.moveTo(0, 0); p.lineTo(0, 4*cm); p.lineTo(4*cm, 0); p.close()
    canv.drawPath(p, fill=1, stroke=0)
    canv.setFillColor(EMERALD)
    p = canv.beginPath()
    p.moveTo(PAGE_W, PAGE_H); p.lineTo(PAGE_W, PAGE_H - 3*cm); p.lineTo(PAGE_W - 3*cm, PAGE_H); p.close()
    canv.drawPath(p, fill=1, stroke=0)

def draw_body_chrome(canv, doc):
    canv.setFillColor(NAVY)
    canv.rect(0, PAGE_H - 1.4*cm, PAGE_W, 1.4*cm, fill=1, stroke=0)
    canv.setFillColor(WHITE)
    canv.setFont(BOLD_FONT, 10.5)
    canv.drawString(MARGIN_X, PAGE_H - 0.9*cm, 'EventWOS')
    canv.setFont(BASE_FONT, 9)
    canv.setFillColor(colors.HexColor('#c7d2fe'))
    canv.drawString(MARGIN_X + 2.2*cm, PAGE_H - 0.9*cm, '. Operations Handbook 2026')
    if current_chapter['title']:
        canv.setFillColor(WHITE)
        canv.setFont(BASE_FONT, 9)
        chap_txt = "{:02d}  {}".format(current_chapter['num'], current_chapter['title'].upper())
        canv.drawRightString(PAGE_W - MARGIN_X, PAGE_H - 0.9*cm, chap_txt)
    canv.setStrokeColor(EMERALD)
    canv.setLineWidth(2)
    canv.line(0, PAGE_H - 1.4*cm - 1, PAGE_W, PAGE_H - 1.4*cm - 1)
    canv.setFillColor(SLATE_500)
    canv.setFont(BASE_FONT, 8)
    canv.drawString(MARGIN_X, 1.0*cm, 'EventWOS . Confidential Operations Handbook')
    canv.drawRightString(PAGE_W - MARGIN_X, 1.0*cm, 'Page {}'.format(doc.page))
    canv.setStrokeColor(SLATE_200)
    canv.setLineWidth(0.5)
    canv.line(MARGIN_X, 1.35*cm, PAGE_W - MARGIN_X, 1.35*cm)

def draw_chapter_bg(canv, doc):
    canv.setFillColor(NAVY_DARK)
    canv.rect(0, 0, PAGE_W, PAGE_H, fill=1, stroke=0)
    canv.setFillColor(EMERALD)
    canv.rect(0, 0, PAGE_W, 0.5*cm, fill=1, stroke=0)
    canv.setFillColor(INDIGO)
    canv.rect(0, PAGE_H - 0.5*cm, PAGE_W, 0.5*cm, fill=1, stroke=0)

styles = getSampleStyleSheet()

def st(name, **kw):
    base = ParagraphStyle(name, parent=styles['Normal'])
    for k, v in kw.items():
        setattr(base, k, v)
    return base

S = {
    'cover_title': st('CoverTitle', fontName=BOLD_FONT, fontSize=44, leading=48, textColor=WHITE, alignment=TA_CENTER),
    'cover_sub':   st('CoverSub',   fontName=BASE_FONT, fontSize=16, leading=22, textColor=colors.HexColor('#c7d2fe'), alignment=TA_CENTER),
    'cover_tag':   st('CoverTag',   fontName=BOLD_FONT, fontSize=12, leading=16, textColor=EMERALD, alignment=TA_CENTER),
    'cover_ver':   st('CoverVer',   fontName=BASE_FONT, fontSize=10, leading=14, textColor=colors.HexColor('#94a3b8'), alignment=TA_CENTER),
    'chapter_num':   st('ChapNum',   fontName=BOLD_FONT, fontSize=90, leading=90, textColor=EMERALD, alignment=TA_LEFT),
    'chapter_title': st('ChapTitle', fontName=BOLD_FONT, fontSize=32, leading=36, textColor=WHITE,   alignment=TA_LEFT),
    'chapter_tag':   st('ChapTag',   fontName=BASE_FONT, fontSize=13, leading=18, textColor=colors.HexColor('#94a3b8'), alignment=TA_LEFT),
    'h1':   st('H1',   fontName=BOLD_FONT, fontSize=22, leading=26, textColor=NAVY,   spaceBefore=6, spaceAfter=8),
    'h2':   st('H2',   fontName=BOLD_FONT, fontSize=15, leading=19, textColor=NAVY,   spaceBefore=14, spaceAfter=6),
    'h3':   st('H3',   fontName=BOLD_FONT, fontSize=12, leading=15, textColor=INDIGO, spaceBefore=10, spaceAfter=4),
    'body': st('Body', fontName=BASE_FONT, fontSize=10.5, leading=15, textColor=SLATE_700, alignment=TA_JUSTIFY, spaceAfter=6),
    'bodyleft': st('BodyLeft', fontName=BASE_FONT, fontSize=10.5, leading=15, textColor=SLATE_700, alignment=TA_LEFT, spaceAfter=6),
    'small': st('Small', fontName=BASE_FONT, fontSize=9, leading=12, textColor=SLATE_500),
    'callout_title': st('CalloutTitle', fontName=BOLD_FONT, fontSize=11, leading=14, textColor=NAVY),
    'callout_body':  st('CalloutBody',  fontName=BASE_FONT, fontSize=10, leading=13.5, textColor=SLATE_700),
    'toc_num': st('TocNum', fontName=BOLD_FONT, fontSize=10, leading=16, textColor=EMERALD),
    'toc_row': st('TocRow', fontName=BASE_FONT, fontSize=11, leading=18, textColor=SLATE_900),
    'toc_sub': st('TocSub', fontName=BASE_FONT, fontSize=9.5, leading=14, textColor=SLATE_500, leftIndent=18),
    'toc_pg':  st('TocPg',  fontName=BOLD_FONT, fontSize=10, leading=16, textColor=NAVY, alignment=TA_RIGHT),
}

class HR(Flowable):
    def __init__(self, color=SLATE_200, thickness=0.5, width=None):
        Flowable.__init__(self); self.color=color; self.thickness=thickness; self.width=width
    def wrap(self, aw, ah):
        self._w = self.width or aw
        return (self._w, self.thickness + 2)
    def draw(self):
        c = self.canv
        c.setStrokeColor(self.color); c.setLineWidth(self.thickness)
        c.line(0, 1, self._w, 1)

class Callout(Flowable):
    def __init__(self, title, body, color=INDIGO, tint=INDIGO_SOFT, width=None):
        Flowable.__init__(self)
        self.title=title; self.body=body; self.color=color; self.tint=tint; self.width=width
        self._title_p = Paragraph('<b>{}</b>'.format(title), S['callout_title'])
        self._body_p  = Paragraph(body, S['callout_body'])
    def wrap(self, aw, ah):
        self._w = self.width or aw
        inner_w = self._w - 24
        tw, th = self._title_p.wrap(inner_w, ah)
        bw, bh = self._body_p.wrap(inner_w, ah)
        self._h  = th + bh + 22
        self._th = th; self._bh = bh
        return (self._w, self._h)
    def draw(self):
        c = self.canv
        c.setFillColor(self.tint); c.setStrokeColor(self.tint)
        c.roundRect(0, 0, self._w, self._h, 4, fill=1, stroke=0)
        c.setFillColor(self.color); c.rect(0, 0, 4, self._h, fill=1, stroke=0)
        y = self._h - 10 - self._th
        self._title_p.drawOn(c, 14, y)
        y -= (self._bh + 4)
        self._body_p.drawOn(c, 14, y)

class FlowChart(Flowable):
    def __init__(self, nodes, width=None, node_h=44, gap=22):
        Flowable.__init__(self)
        self.nodes=nodes; self.width=width; self.node_h=node_h; self.gap=gap
    def wrap(self, aw, ah):
        self._w = self.width or aw
        self._h = len(self.nodes) * self.node_h + (len(self.nodes) - 1) * self.gap
        return (self._w, self._h)
    def draw(self):
        c = self.canv
        box_w = min(self._w, 360)
        x = (self._w - box_w) / 2
        y = self._h - self.node_h
        for i, (label, color) in enumerate(self.nodes):
            c.setFillColor(color)
            c.roundRect(x, y, box_w, self.node_h, 8, fill=1, stroke=0)
            c.setFillColor(WHITE)
            label_clean = label.replace('&amp;', '&').replace('&quot;', '"').replace('&lt;', '<').replace('&gt;', '>')
            lines = self._wrap_lines(c, label_clean, box_w - 24, 11)
            total_h = len(lines) * 13
            start_y = y + (self.node_h - total_h) / 2 + total_h - 11
            c.setFont(BOLD_FONT, 11)
            for j, ln in enumerate(lines):
                c.drawCentredString(x + box_w/2, start_y - j*13, ln)
            if i < len(self.nodes) - 1:
                ax = self._w / 2
                ay_top = y - 2
                ay_bot = y - self.gap + 2
                c.setStrokeColor(SLATE_300); c.setLineWidth(1.6)
                c.line(ax, ay_top, ax, ay_bot + 6)
                c.setFillColor(SLATE_500)
                pth = c.beginPath()
                pth.moveTo(ax, ay_bot); pth.lineTo(ax - 4, ay_bot + 7); pth.lineTo(ax + 4, ay_bot + 7); pth.close()
                c.drawPath(pth, fill=1, stroke=0)
            y -= (self.node_h + self.gap)
    def _wrap_lines(self, c, txt, max_w, font_size):
        words = txt.split()
        lines, cur = [], ''
        for w in words:
            trial = (cur + ' ' + w).strip()
            if c.stringWidth(trial, BOLD_FONT, font_size) <= max_w:
                cur = trial
            else:
                if cur: lines.append(cur)
                cur = w
        if cur: lines.append(cur)
        return lines or [txt]

class RolePersona(Flowable):
    def __init__(self, roles, width=None, h=110):
        Flowable.__init__(self); self.roles=roles; self.width=width; self.h=h
    def wrap(self, aw, ah):
        self._w = self.width or aw
        return (self._w, self.h + 6)
    def draw(self):
        c = self.canv
        n = len(self.roles); gap = 10
        card_w = (self._w - (n-1)*gap) / n
        for i, (initial, name, one_liner, color) in enumerate(self.roles):
            x = i * (card_w + gap)
            c.setFillColor(WHITE); c.setStrokeColor(SLATE_200)
            c.roundRect(x, 0, card_w, self.h, 8, fill=1, stroke=1)
            cx = x + 22; cy = self.h - 24
            c.setFillColor(color); c.circle(cx, cy, 14, fill=1, stroke=0)
            c.setFillColor(WHITE); c.setFont(BOLD_FONT, 15)
            c.drawCentredString(cx, cy - 5, initial)
            c.setFillColor(NAVY); c.setFont(BOLD_FONT, 12)
            c.drawString(x + 44, cy - 3, name)
            c.setFillColor(SLATE_700); c.setFont(BASE_FONT, 9)
            words = one_liner.split(); lines, cur = [], ''
            max_w = card_w - 24
            for w in words:
                trial = (cur + ' ' + w).strip()
                if c.stringWidth(trial, BASE_FONT, 9) <= max_w:
                    cur = trial
                else:
                    if cur: lines.append(cur)
                    cur = w
            if cur: lines.append(cur)
            y = self.h - 50
            for ln in lines[:5]:
                c.drawString(x + 12, y, ln); y -= 12

class KeyValueTable(Flowable):
    def __init__(self, rows, width=None, key_w=140):
        Flowable.__init__(self)
        self.rows=rows; self.width=width; self.key_w=key_w
        self._paras = [(Paragraph('<b>{}</b>'.format(k), S['callout_title']),
                        Paragraph(v, S['callout_body'])) for k, v in rows]
    def wrap(self, aw, ah):
        self._w = self.width or aw
        val_w = self._w - self.key_w - 20
        h = 0; self._h_rows = []
        for kp, vp in self._paras:
            kw_, kh = kp.wrap(self.key_w - 12, ah)
            vw_, vh = vp.wrap(val_w, ah)
            rh = max(kh, vh) + 12
            self._h_rows.append(rh); h += rh
        self._h = h
        return (self._w, self._h)
    def draw(self):
        c = self.canv
        c.setStrokeColor(SLATE_200); c.setFillColor(WHITE)
        c.roundRect(0, 0, self._w, self._h, 4, fill=1, stroke=1)
        y = self._h
        for i, ((kp, vp), rh) in enumerate(zip(self._paras, self._h_rows)):
            y -= rh
            if i > 0:
                c.setStrokeColor(SLATE_100)
                c.line(6, y + rh, self._w - 6, y + rh)
            c.setFillColor(SLATE_50); c.rect(0, y, self.key_w, rh, fill=1, stroke=0)
            kw_, kh = kp.wrap(self.key_w - 12, rh)
            kp.drawOn(c, 8, y + rh - kh - 6)
            vw_, vh = vp.wrap(self._w - self.key_w - 20, rh)
            vp.drawOn(c, self.key_w + 10, y + rh - vh - 6)

class ChecklistBox(Flowable):
    def __init__(self, title, items, width=None, color=EMERALD, tint=EMERALD_SFT):
        Flowable.__init__(self)
        self.title=title; self.items=items; self.width=width; self.color=color; self.tint=tint
        self._title_p = Paragraph('<b>{}</b>'.format(title), S['callout_title'])
        self._item_ps = [Paragraph(it, S['callout_body']) for it in items]
    def wrap(self, aw, ah):
        self._w = self.width or aw
        inner_w = self._w - 40
        tw, th = self._title_p.wrap(inner_w, ah)
        self._th = th; self._ih = []
        h = th + 18
        for pp in self._item_ps:
            pw, ph = pp.wrap(inner_w - 18, ah)
            self._ih.append(ph); h += ph + 8
        self._h = h + 8
        return (self._w, self._h)
    def draw(self):
        c = self.canv
        c.setFillColor(self.tint); c.setStrokeColor(self.color); c.setLineWidth(0.6)
        c.roundRect(0, 0, self._w, self._h, 6, fill=1, stroke=1)
        y = self._h - 12 - self._th
        c.setFillColor(NAVY)
        self._title_p.drawOn(c, 16, y)
        y -= 12
        for pp, ph in zip(self._item_ps, self._ih):
            y -= ph
            box_y = y + ph - 12
            c.setFillColor(WHITE); c.setStrokeColor(self.color); c.setLineWidth(1.2)
            c.rect(16, box_y, 10, 10, fill=1, stroke=1)
            pp.drawOn(c, 34, y)
            y -= 8

class SetChapterFlowable(Flowable):
    def __init__(self, num, title):
        Flowable.__init__(self); self.num=num; self.title=title
    def wrap(self, aw, ah): return (0, 0)
    def draw(self):
        current_chapter['num'] = self.num
        current_chapter['title'] = self.title

story = []

def set_chapter(num, title):
    story.append(SetChapterFlowable(num, title))

def p(text, style='body'):
    story.append(Paragraph(text, S[style]))

def bullets(items, style='body'):
    lf = ListFlowable(
        [ListItem(Paragraph(x, S[style]), leftIndent=10, value='\u2022') for x in items],
        bulletType='bullet', leftIndent=14, bulletFontName=BOLD_FONT,
        bulletFontSize=11, bulletColor=EMERALD, spaceBefore=2, spaceAfter=6
    )
    story.append(lf)

def gap(h=8):
    story.append(Spacer(1, h))

def hr(**kw):
    story.append(HR(**kw))
