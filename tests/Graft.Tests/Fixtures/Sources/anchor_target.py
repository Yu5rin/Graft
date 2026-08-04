class Report:
    def __init__(self, title):
        self.title = title
        self.rows = []

    def add_row(self, row):
        self.rows.append(row)

    def render(self):
        lines = [self.title]
        for row in self.rows:
            lines.append(str(row))
        return lines

    def clear(self):
        self.rows = []
