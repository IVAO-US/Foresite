window.getWidth = () => window.innerWidth;
window.getHeight = () => window.innerHeight;
window.getScale = () => scale;

const tau = 2 * Math.PI;
let scale = 1, isMouseDown = false, leftEdge = 0, topEdge = 0, bufferWidth = 1000, bufferHeight = 1000;
window.initCanvas = (bufWidth, bufHeight) => {
	const display = document.querySelector("#displayCanvas");
	display.addEventListener('wheel', we => {
		const oldScale = scale;
		scale += we.deltaY / 100; scale = Math.max(Math.min(scale, 5), 0.005);
		leftEdge -= window.innerWidth / 2 * (scale - oldScale);
		topEdge -= window.innerHeight / 2 * (scale - oldScale);
	}, { passive: true });
	display.addEventListener('mousedown', _ => { isMouseDown = true; }, { passive: true });
	display.addEventListener('mouseup', _ => { isMouseDown = false; }, { passive: true });
	display.addEventListener('mousemove', me => {
		if (isMouseDown === false) return;
		leftEdge -= me.movementX * scale * 0.8;
		topEdge -= me.movementY * scale * 0.8;
	}, { passive: true });

	bufferWidth = bufWidth;
	bufferHeight = bufHeight;
	leftEdge = 0;
	topEdge = 0;
	requestAnimationFrame(window.draw);
}
window.draw = () => {
	const display = document.querySelector("#displayCanvas");
	const coast = document.querySelector("#coastCanvas");
	const line = document.querySelector("#lineCanvas");
	const point = document.querySelector("#pointCanvas");

	const ctx = display.getContext('2d');
	ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);
	ctx.drawImage(coast, leftEdge, topEdge, window.innerWidth * scale, window.innerHeight * scale, 0, 0, window.innerWidth, window.innerHeight);
	ctx.drawImage(line, leftEdge, topEdge, window.innerWidth * scale, window.innerHeight * scale, 0, 0, window.innerWidth, window.innerHeight);
	ctx.drawImage(point, leftEdge, topEdge, window.innerWidth * scale, window.innerHeight * scale, 0, 0, window.innerWidth, window.innerHeight);
	ctx.fillStyle = "white";
	ctx.fillRect(window.innerWidth / 2 - 25, window.innerHeight / 2 - 25, 50, 50);

	requestAnimationFrame(window.draw);
	console.log(`(${leftEdge}, ${topEdge})`);
}
window.drawCoastline = (lines) => {
	const ctx = document.querySelector("#coastCanvas").getContext('2d');
	ctx.clearRect(0, 0, bufferWidth, bufferHeight);

	ctx.strokeStyle = "white";
	ctx.beginPath();

	for (const line of lines) {
		if (line.length < 3) continue;

		ctx.moveTo(line[0][0], line[0][1]);
		for (const point of line.slice(1)) {
			ctx.lineTo(point[0], point[1]);
		}
	}

	ctx.stroke();
}
window.drawLines = (lines) => {
	const ctx = document.querySelector("#lineCanvas").getContext('2d');
	ctx.clearRect(0, 0, bufferWidth, bufferHeight);

	ctx.strokeStyle = "white";
	ctx.beginPath();

	for (const line of lines) {
		if (line.length < 3) continue;

		ctx.moveTo(line[0][0], line[0][1]);
		for (const point of line.slice(1)) {
			ctx.lineTo(point[0], point[1]);
		}
	}

	ctx.stroke();
}
window.drawPoints = (points) => {
	const ctx = document.querySelector("#pointCanvas").getContext('2d');
	ctx.clearRect(0, 0, bufferWidth, bufferHeight);

	ctx.fillStyle = "blue";
	for (const point of points) {
		const [x, y, radius] = point;
		ctx.beginPath();
		ctx.arc(x, y, radius, 0, tau);
		ctx.fill();
	}
	ctx.stroke();
}